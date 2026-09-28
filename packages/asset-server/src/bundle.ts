import { mkdir, open, readFile, stat, writeFile } from "node:fs/promises";
import { dirname, join, posix } from "node:path";
import { crc32 } from "node:zlib";
import { BUNDLE_MANIFEST_NAME, menuAssets, parseManifest } from "@openvideomenu/core";

/**
 * Bundles (format spec §2.1): a ZIP holding `manifest.json` and its assets, every entry stored
 * uncompressed, so a member is one run of bytes in the file that can be served (and ranged) as is.
 * Just enough ZIP to read that, and to write it (`openvideomenu pack`). No dependencies.
 */

/** One member of a bundle. `dataOffset` is resolved from its local header when first needed. */
export interface BundleMember {
  name: string;
  /** 0 = stored (the only method bundles may use). */
  method: number;
  size: number;
  headerOffset: number;
  encrypted: boolean;
  dataOffset?: number;
}

export interface Bundle {
  members: Map<string, BundleMember>;
  /** Problems that make the bundle unusable or unsafe (names escaping it, compressed entries…). */
  errors: string[];
}

const EOCD = 0x06054b50;
const EOCD64 = 0x06064b50;
const EOCD64_LOCATOR = 0x07064b50;
const CENTRAL = 0x02014b50;
const LOCAL = 0x04034b50;

/** Read a bundle's table of contents (its central directory) and check its entry names and methods. */
export async function readBundle(file: string): Promise<Bundle> {
  const fd = await open(file, "r");
  try {
    const size = (await fd.stat()).size;
    // The end record is in the last 22 bytes plus up to 64 KB of comment.
    const tailSize = Math.min(size, 22 + 0xffff + 20);
    const tail = Buffer.alloc(tailSize);
    await fd.read(tail, 0, tailSize, size - tailSize);
    let eocd = -1;
    for (let i = tailSize - 22; i >= 0; i--) if (tail.readUInt32LE(i) === EOCD) { eocd = i; break; }
    if (eocd < 0) throw new Error("Not a ZIP file (no end of central directory).");
    let count = tail.readUInt16LE(eocd + 10);
    let cdSize = tail.readUInt32LE(eocd + 12);
    let cdOffset = tail.readUInt32LE(eocd + 16);
    // ZIP64: the real numbers are in the ZIP64 end record, found through its locator.
    if (eocd >= 20 && tail.readUInt32LE(eocd - 20) === EOCD64_LOCATOR) {
      const at = Number(tail.readBigUInt64LE(eocd - 20 + 8));
      const rec = Buffer.alloc(56);
      await fd.read(rec, 0, 56, at);
      if (rec.readUInt32LE(0) === EOCD64) {
        count = Number(rec.readBigUInt64LE(32));
        cdSize = Number(rec.readBigUInt64LE(40));
        cdOffset = Number(rec.readBigUInt64LE(48));
      }
    }
    const cd = Buffer.alloc(cdSize);
    await fd.read(cd, 0, cdSize, cdOffset);

    const members = new Map<string, BundleMember>();
    const errors: string[] = [];
    let p = 0;
    for (let n = 0; n < count; n++) {
      if (cd.readUInt32LE(p) !== CENTRAL) throw new Error("Corrupt central directory.");
      const flags = cd.readUInt16LE(p + 8);
      const method = cd.readUInt16LE(p + 10);
      let size32: number = cd.readUInt32LE(p + 24);
      let headerOffset: number = cd.readUInt32LE(p + 42);
      const nameLen = cd.readUInt16LE(p + 28);
      const extraLen = cd.readUInt16LE(p + 30);
      const commentLen = cd.readUInt16LE(p + 32);
      const name = cd.toString(flags & 0x800 ? "utf8" : "latin1", p + 46, p + 46 + nameLen);
      // ZIP64 extra field: 64-bit values for whichever 32-bit fields are 0xFFFFFFFF, in order.
      let e = p + 46 + nameLen;
      const extraEnd = e + extraLen;
      while (e + 4 <= extraEnd) {
        const id = cd.readUInt16LE(e);
        const len = cd.readUInt16LE(e + 2);
        if (id === 0x0001) {
          let q = e + 4;
          if (size32 === 0xffffffff) { size32 = Number(cd.readBigUInt64LE(q)); q += 8; }
          if (cd.readUInt32LE(p + 20) === 0xffffffff) q += 8; // compressed size (equals size when stored)
          if (headerOffset === 0xffffffff) headerOffset = Number(cd.readBigUInt64LE(q));
        }
        e += 4 + len;
      }
      p = extraEnd + commentLen;
      if (name.endsWith("/")) continue; // a folder entry
      const normalized = posix.normalize(name);
      if (name.includes("\\") || posix.isAbsolute(name) || normalized.startsWith("../") || normalized === "..") {
        errors.push(`Entry name not allowed (absolute, backslashes or ".."): ${name}`);
        continue;
      }
      const member = { name: normalized, method, size: size32, headerOffset, encrypted: (flags & 1) === 1 };
      if (member.encrypted) errors.push(`Encrypted entry: ${name}`);
      else if (method !== 0) errors.push(`Compressed entry (bundles store every entry uncompressed): ${name}`);
      members.set(normalized, member);
    }
    return { members, errors };
  } finally {
    await fd.close();
  }
}

/** Where a stored member's bytes start in the bundle file (read from its local header once). */
export async function memberDataOffset(file: string, member: BundleMember): Promise<number> {
  if (member.dataOffset !== undefined) return member.dataOffset;
  const fd = await open(file, "r");
  try {
    const header = Buffer.alloc(30);
    await fd.read(header, 0, 30, member.headerOffset);
    if (header.readUInt32LE(0) !== LOCAL) throw new Error(`Corrupt local header for ${member.name}`);
    member.dataOffset = member.headerOffset + 30 + header.readUInt16LE(26) + header.readUInt16LE(28);
    return member.dataOffset;
  } finally {
    await fd.close();
  }
}

/** A stored member's bytes (for small members such as manifest.json). */
export async function readMember(file: string, member: BundleMember): Promise<Buffer> {
  const start = await memberDataOffset(file, member);
  const fd = await open(file, "r");
  try {
    const data = Buffer.alloc(member.size);
    await fd.read(data, 0, member.size, start);
    return data;
  } finally {
    await fd.close();
  }
}

// --- writing (pack) -----------------------------------------------------------------------------

/**
 * Pack a manifest and the local assets it names into a bundle: `manifest.json` plus each asset at
 * its path, all stored. Assets outside the manifest's folder (paths with "..") can't go inside a
 * bundle and are reported. Returns the problems found (missing assets, assets outside the folder).
 */
export async function packBundle(manifestFile: string, outFile: string): Promise<{ written: number; problems: string[] }> {
  const text = await readFile(manifestFile, "utf8");
  const parsed = parseManifest(text);
  if (!parsed.ok) throw new Error(`The manifest has errors: ${parsed.errors.map((e) => `${e.path || "/"}: ${e.message}`).join("; ")}`);
  const dir = dirname(manifestFile);
  const problems: string[] = [];
  const files: { name: string; data: Buffer }[] = [{ name: BUNDLE_MANIFEST_NAME, data: Buffer.from(text, "utf8") }];
  for (const asset of [...new Set(menuAssets(parsed.manifest!))].sort()) {
    const name = posix.normalize(asset);
    if (name.startsWith("../") || posix.isAbsolute(name)) {
      problems.push(`Outside the manifest's folder, so not bundled: ${asset}`);
      continue;
    }
    try {
      files.push({ name, data: await readFile(join(dir, ...name.split("/"))) });
    } catch {
      problems.push(`Menu asset not found: ${asset}`);
    }
  }
  await writeZipStored(outFile, files);
  return { written: files.length, problems };
}

/** Unpack a bundle into [outDir] as `<name>.openvideomenu.json` plus its assets. */
export async function unpackBundle(bundleFile: string, outDir: string, manifestName: string): Promise<number> {
  const bundle = await readBundle(bundleFile);
  if (bundle.errors.length) throw new Error(bundle.errors.join("; "));
  let n = 0;
  for (const member of bundle.members.values()) {
    const target = member.name === BUNDLE_MANIFEST_NAME ? join(outDir, manifestName) : join(outDir, ...member.name.split("/"));
    await mkdir(dirname(target), { recursive: true });
    await writeFile(target, await readMember(bundleFile, member));
    n++;
  }
  return n;
}

/** A ZIP with every entry stored (method 0), UTF-8 names, no compression, no ZIP64 (under 4 GB). */
async function writeZipStored(outFile: string, files: { name: string; data: Buffer }[]) {
  const parts: Buffer[] = [];
  const central: Buffer[] = [];
  let offset = 0;
  const now = new Date();
  const dosTime = (now.getHours() << 11) | (now.getMinutes() << 5) | Math.floor(now.getSeconds() / 2);
  const dosDate = ((now.getFullYear() - 1980) << 9) | ((now.getMonth() + 1) << 5) | now.getDate();
  for (const { name, data } of files) {
    const nameBuf = Buffer.from(name, "utf8");
    const crc = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(LOCAL, 0);
    local.writeUInt16LE(10, 4); // version needed
    local.writeUInt16LE(0x800, 6); // UTF-8 names
    local.writeUInt16LE(0, 8); // stored
    local.writeUInt16LE(dosTime, 10);
    local.writeUInt16LE(dosDate, 12);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(data.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(nameBuf.length, 26);
    local.writeUInt16LE(0, 28);
    const entry = Buffer.alloc(46);
    entry.writeUInt32LE(CENTRAL, 0);
    entry.writeUInt16LE(20, 4); // made by
    entry.writeUInt16LE(10, 6);
    entry.writeUInt16LE(0x800, 8);
    entry.writeUInt16LE(0, 10);
    entry.writeUInt16LE(dosTime, 12);
    entry.writeUInt16LE(dosDate, 14);
    entry.writeUInt32LE(crc, 16);
    entry.writeUInt32LE(data.length, 20);
    entry.writeUInt32LE(data.length, 24);
    entry.writeUInt16LE(nameBuf.length, 28);
    entry.writeUInt32LE(offset, 42);
    central.push(entry, nameBuf);
    parts.push(local, nameBuf, data);
    offset += 30 + nameBuf.length + data.length;
    if (offset > 0xffffffff) throw new Error("Bundles over 4 GB need ZIP64, which pack doesn't write yet.");
  }
  const cd = Buffer.concat(central);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(EOCD, 0);
  end.writeUInt16LE(files.length, 8);
  end.writeUInt16LE(files.length, 10);
  end.writeUInt32LE(cd.length, 12);
  end.writeUInt32LE(offset, 16);
  await writeFile(outFile, Buffer.concat([...parts, cd, end]));
}

/** Size of a file, for callers that only have a path. */
export const fileSize = async (file: string) => (await stat(file)).size;
