import { createHash } from "node:crypto";
import { readFile, stat } from "node:fs/promises";
import { gzipSync } from "node:zlib";
import { isAbsolute, join, posix, relative, resolve } from "node:path";
import { BUNDLE_MANIFEST_NAME, FORMAT_VERSION, isBundleFileName, MANIFEST_SUFFIX, BUNDLE_EXTENSION, menuAssets, parseManifest, type MediaRef } from "@openvideomenu/core";
import { memberDataOffset, readBundle, readMember, type BundleMember } from "./bundle.js";
import { scanManifests } from "./files.js";
import { checkAssetFiles, checkIgnoreFiles, eachLimited, isFaststart } from "./media-check.js";

export interface CatalogMedia {
  id: string;
  kind?: string;
  label?: string;
  /** Absent for media made of segments (Level 2): see `segments`. */
  ref?: MediaRef;
  /** Level 2: the media ids a media made of segments plays, in order (spec 4.2). */
  segments?: string[];
  /**
   * For relative-path refs: the file's path relative to the media root, forward slashes. TV apps
   * match its trailing segments against the file paths their media server reports.
   */
  path?: string;
  /** Level 2: the media's other angles as files (angle 2, 3, …), each with its ref and path as above. */
  angles?: { ref: MediaRef; path?: string }[];
}

export interface CatalogEntry {
  /** Stable id for this manifest (hash of root + path). */
  id: string;
  root: number;
  /** Manifest path relative to its media root, forward slashes; for a bundle, the bundle's own path. */
  path: string;
  /** URL path (under /v1/files/) the manifest is served at; relative asset paths resolve from it. */
  url: string;
  valid: boolean;
  errors?: string[];
  /** Problems that don't stop the menu working everywhere: missing menu art, MP4s without faststart. */
  warnings?: string[];
  version?: string;
  title?: { name: string; year?: number; ids?: Record<string, string | undefined> };
  source?: { kind: string; generator?: string };
  hasMenus: boolean;
  media: CatalogMedia[];
  /**
   * Menu assets, relative to the media root; a bundle's members appear under its path, as if it were
   * a folder ("Movies/X/X.ovmz/menus/a.jpg"). Kept on the server (it's the allowlist); not in the index.
   */
  assets: string[];
  modified: string;
}

/** What TV apps get per manifest: enough to match titles and open menus, without the asset list. */
export type IndexEntry = Omit<CatalogEntry, "assets" | "warnings">;

/** The index as sent: JSON, a gzipped copy, and an ETag, made once per catalog change. */
export interface IndexBody {
  json: string;
  gzip: Buffer;
  etag: string;
}

interface Cached {
  mtimeMs: number;
  entry: CatalogEntry;
  /** For a bundle: its members, by name. */
  members?: Map<string, BundleMember>;
  /** Asset checks (files present, MP4s faststart) done for this version of the manifest. */
  checked?: boolean;
}

/** Encode each path segment for a URL, keeping the slashes. */
export const encodePath = (path: string) => path.split("/").map(encodeURIComponent).join("/");

/**
 * The manifests under the media roots, and the exact set of files that may be served: the manifests
 * themselves plus the menu assets they name. Nothing else — in particular no media — is reachable.
 */
export class Catalog {
  private cache = new Map<string, Cached>();
  private list: CatalogEntry[] = [];
  private allowed = new Set<string>();
  /** Bundles by "root:path": the file and its members, for serving members as files. */
  private bundles = new Map<string, { file: string; members: Map<string, BundleMember> }>();
  private body?: IndexBody;
  private scanning?: Promise<void>;
  private firstScan?: Promise<void>;
  private timer?: NodeJS.Timeout;
  /** Roots set by [setRoots], used from the next scan on. */
  private nextRoots?: string[];

  /** Called after each scan with manifests whose asset checks found problems (first time seen or changed). */
  onWarnings?: (entries: CatalogEntry[]) => void;

  /** The media folders, in the order their index numbers ("root") refer to. */
  roots: string[];

  constructor(
    roots: string[],
    // A scan reuses unchanged manifests (by modified time), so it is cheap: a home library takes about a second.
    private readonly rescanMs = 30_000,
  ) {
    this.roots = roots.map((r) => resolve(r));
  }

  /**
   * Change the media folders (the media server's libraries changed). The next scan uses them, and swaps
   * them in together with its results, so a file is never looked up under the wrong root. False when
   * they're the same as now.
   */
  setRoots(roots: string[]): boolean {
    const next = roots.map((r) => resolve(r));
    const current = this.nextRoots ?? this.roots;
    if (next.length === current.length && next.every((r, i) => r === current[i])) return false;
    this.nextRoots = next;
    return true;
  }

  /**
   * Scan now and every [rescanMs] in the background. Requests never wait for a rescan; they only
   * wait for the first scan (see [ready]).
   */
  start(): Promise<void> {
    this.timer ??= setInterval(() => void this.scan(), this.rescanMs);
    this.timer.unref?.();
    return this.ready();
  }

  stop() {
    clearInterval(this.timer);
    this.timer = undefined;
  }

  /** Resolves once the catalog has been scanned at least once. */
  ready(): Promise<void> {
    this.firstScan ??= this.scan().then(() => void (this.isReady = true));
    return this.firstScan;
  }

  /** True once the first scan has finished. */
  isReady = false;

  /** The manifests from the latest finished scan. */
  entries(): CatalogEntry[] {
    return this.list;
  }

  /** The index TV apps download: serialized, gzipped and tagged once per change, not per request. */
  index(meta: Record<string, unknown>): IndexBody {
    if (!this.body) {
      const manifests: IndexEntry[] = this.list.map(({ assets: _a, warnings: _w, ...rest }) => rest);
      const json = JSON.stringify({ ...meta, manifests });
      this.body = { json, gzip: gzipSync(json), etag: `"${createHash("sha1").update(json).digest("hex").slice(0, 20)}"` };
    }
    return this.body;
  }

  /**
   * An allowed file, or undefined. `path` is root-relative with forward slashes. A bundle member
   * ("…/X.ovmz/menus/a.jpg") comes back as the bundle file plus the slice holding the member.
   */
  async resolveFile(root: number, path: string): Promise<{ file: string; slice?: { name: string; start: number; size: number } } | undefined> {
    const normalized = posix.normalize(path);
    if (normalized.startsWith("../") || normalized === ".." || posix.isAbsolute(normalized)) return undefined;
    if (!this.allowed.has(`${root}:${normalized}`)) return undefined;
    const base = this.roots[root];
    if (base === undefined) return undefined;
    // Inside a bundle: the part up to the first ".ovmz/" segment that is a bundle in the catalog.
    const segments = normalized.split("/");
    for (let i = 0; i < segments.length - 1; i++) {
      if (!isBundleFileName(segments[i]!)) continue;
      const bundle = this.bundles.get(`${root}:${segments.slice(0, i + 1).join("/")}`);
      const member = bundle?.members.get(segments.slice(i + 1).join("/"));
      if (!bundle || !member) return undefined;
      return { file: bundle.file, slice: { name: member.name, start: await memberDataOffset(bundle.file, member), size: member.size } };
    }
    const full = resolve(base, ...segments);
    // Containment via relative(): a plain prefix check breaks on roots that resolve with a trailing
    // separator, such as UNC shares ("\\server\share\") and drive roots ("D:\").
    const rel = relative(base, full);
    return rel !== "" && !rel.startsWith("..") && !isAbsolute(rel) ? { file: full } : undefined;
  }

  /**
   * Find manifests and read the new or changed ones; unchanged ones (same modified time) are reused.
   * The list is published as soon as manifests are read. Asset checks run afterwards and only for
   * new or changed manifests, so they never hold up the index. Concurrent calls share one scan.
   */
  scan(): Promise<void> {
    this.scanning ??= this.doScan().finally(() => (this.scanning = undefined));
    return this.scanning;
  }

  private async doScan() {
    const roots = this.nextRoots ?? this.roots;
    // New roots number differently: nothing cached under the old numbers carries over.
    const previous = roots === this.roots ? this.cache : new Map<string, Cached>();
    const found: { key: string; root: number; path: string; file: string }[] = [];
    await Promise.all(
      roots.map(async (root, rootIndex) => {
        let paths: string[];
        try {
          paths = await scanManifests(root);
        } catch {
          return; // root missing or unreadable (e.g. share offline)
        }
        for (const path of paths) found.push({ key: `${rootIndex}:${path}`, root: rootIndex, path, file: join(root, ...path.split("/")) });
      }),
    );
    found.sort((a, b) => a.root - b.root || (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));
    // A folder with both X.ovmz and X.openvideomenu.json: the bundle is the one to use (spec §2.1).
    const bundlePaths = new Set(found.filter((f) => isBundleFileName(f.path)).map((f) => `${f.root}:${f.path.slice(0, -BUNDLE_EXTENSION.length).toLowerCase()}`));
    const chosen = found.filter(
      (f) => isBundleFileName(f.path) || !bundlePaths.has(`${f.root}:${f.path.slice(0, -MANIFEST_SUFFIX.length).toLowerCase()}`),
    );

    const loaded = await eachLimited(chosen, 16, async (f) => {
      let mtimeMs: number;
      try {
        mtimeMs = (await stat(f.file)).mtimeMs;
      } catch {
        return undefined;
      }
      const cached = previous.get(f.key);
      if (cached && cached.mtimeMs === mtimeMs) return { key: f.key, root: f.root, file: f.file, cached };
      return { key: f.key, root: f.root, file: f.file, cached: await this.load(f.root, f.path, f.file, mtimeMs) };
    });

    const next: CatalogEntry[] = [];
    const allowed = new Set<string>();
    const cache = new Map<string, Cached>();
    const bundles = new Map<string, { file: string; members: Map<string, BundleMember> }>();
    for (const l of loaded) {
      if (!l) continue;
      cache.set(l.key, l.cached);
      next.push(l.cached.entry);
      if (l.cached.members) {
        bundles.set(l.key, { file: l.file, members: l.cached.members });
        if (l.cached.entry.valid) allowed.add(`${l.key}/${BUNDLE_MANIFEST_NAME}`);
      } else {
        allowed.add(l.key);
      }
      for (const asset of l.cached.entry.assets) allowed.add(`${l.root}:${asset}`);
    }
    const changed = roots !== this.roots || next.length !== this.list.length || next.some((e, i) => e !== this.list[i]);
    this.roots = roots;
    if (this.nextRoots === roots) this.nextRoots = undefined;
    this.cache = cache;
    this.list = next;
    this.allowed = allowed;
    this.bundles = bundles;
    if (changed) this.body = undefined;

    // Asset checks for new or changed manifests; problems go to the log, not the index.
    const unchecked = [...cache.values()].filter((c) => !c.checked && c.entry.valid);
    const warned: CatalogEntry[] = [];
    await eachLimited(unchecked, 4, async (c) => {
      const e = c.entry;
      const bundle = c.members ? { file: join(this.roots[e.root]!, ...e.path.split("/")), members: c.members } : undefined;
      const inBundle = (a: string) => bundle !== undefined && a.startsWith(`${e.path}/`);
      const onDisk = e.assets.filter((a) => !inBundle(a)).map((a) => ({ shown: a, file: join(this.roots[e.root]!, ...a.split("/")) }));
      const problems = [...(await checkAssetFiles(onDisk)), ...(await checkIgnoreFiles(onDisk))];
      // Bundle members: present, and MP4s with their index first (media servers never see inside a
      // bundle, so no ignore files are needed).
      for (const a of e.assets.filter(inBundle)) {
        const member = bundle!.members.get(a.slice(e.path.length + 1));
        if (!member) {
          problems.push(`Menu asset not in the bundle: ${a}`);
          continue;
        }
        const slice = { name: member.name, start: await memberDataOffset(bundle!.file, member), size: member.size };
        if ((await isFaststart(bundle!.file, slice)) === false) {
          problems.push(`${a} has its index at the end of the file, so some TV players stall on it. Fix it (ffmpeg -movflags +faststart) and pack the bundle again.`);
        }
      }
      e.warnings = [...(e.warnings ?? []), ...problems];
      if (e.warnings.length === 0) delete e.warnings;
      else warned.push(e);
      c.checked = true;
    });
    if (warned.length) this.onWarnings?.(warned);
  }

  private async load(root: number, path: string, file: string, mtimeMs: number): Promise<Cached> {
    if (isBundleFileName(path)) return this.loadBundle(root, path, file, mtimeMs);
    let text: string;
    try {
      text = await readFile(file, "utf8");
    } catch (e) {
      return { mtimeMs, entry: { ...this.blank(root, path, mtimeMs), errors: [`Unreadable: ${(e as Error).message}`] } };
    }
    return { mtimeMs, entry: this.entryFor(root, path, text, mtimeMs) };
  }

  /** A bundle: its manifest.json, read from inside it; its members are served as its assets. */
  private async loadBundle(root: number, path: string, file: string, mtimeMs: number): Promise<Cached> {
    const blank = this.blank(root, path, mtimeMs);
    try {
      const bundle = await readBundle(file);
      const manifest = bundle.members.get(BUNDLE_MANIFEST_NAME);
      if (bundle.errors.length || !manifest || manifest.method !== 0) {
        const errors = [...bundle.errors, ...(manifest ? [] : [`No ${BUNDLE_MANIFEST_NAME} at the bundle's root.`])];
        return { mtimeMs, entry: { ...blank, errors }, members: bundle.members };
      }
      const text = (await readMember(file, manifest)).toString("utf8");
      return { mtimeMs, entry: this.entryFor(root, path, text, mtimeMs), members: bundle.members };
    } catch (e) {
      return { mtimeMs, entry: { ...blank, errors: [`Unreadable bundle: ${(e as Error).message}`] } };
    }
  }

  private blank(root: number, path: string, mtimeMs: number): CatalogEntry {
    const id = createHash("sha1").update(`${root}:${path}`).digest("hex").slice(0, 16);
    const url = isBundleFileName(path) ? `/v1/files/${root}/${encodePath(path)}/${BUNDLE_MANIFEST_NAME}` : `/v1/files/${root}/${encodePath(path)}`;
    return { id, root, path, url, valid: false, hasMenus: false, media: [], assets: [], modified: new Date(mtimeMs).toISOString() };
  }

  /**
   * The catalog entry for a manifest's text. Media refs resolve against the folder holding the
   * manifest (or the bundle); assets against the manifest's own location, which for a bundle is
   * inside it ("…/X.ovmz/menus/a.jpg"), as if the bundle were a folder.
   */
  private entryFor(root: number, path: string, text: string, mtimeMs: number): CatalogEntry {
    const base = this.blank(root, path, mtimeMs);
    const result = parseManifest(text);
    if (!result.ok) return { ...base, errors: result.errors.map((e) => `${e.path || "/"}: ${e.message}`) };
    const m = result.manifest!;
    const dir = posix.dirname(path);
    const inRoot = (rel: string): string | undefined => {
      const p = posix.normalize(dir === "." ? rel : `${dir}/${rel}`);
      return p.startsWith("../") || p === ".." ? undefined : p;
    };
    const isRelative = (ref: string) => !/^[a-z][a-z0-9+.-]*:/i.test(ref) && !ref.startsWith("/");
    const assetDir = isBundleFileName(path) ? path : dir;
    const assetInRoot = (rel: string): string | undefined => {
      const p = posix.normalize(assetDir === "." ? rel : `${assetDir}/${rel}`);
      return p.startsWith("../") || p === ".." ? undefined : p;
    };
    const assets = menuAssets(m).map(assetInRoot).filter((p): p is string => p !== undefined);
    const warnings: string[] = [];
    if (m.version !== FORMAT_VERSION && Number(m.version.split(".")[1]) > Number(FORMAT_VERSION.split(".")[1]))
      warnings.unshift(`Written for ${m.version}; this server reads ${FORMAT_VERSION}. Newer fields are ignored.`);
    return {
      ...base,
      valid: true,
      version: m.version,
      title: m.title,
      source: m.source,
      hasMenus: !!m.menus && Object.keys(m.menus).length > 0,
      media: Object.entries(m.media).map(([mid, media]) => ({
        id: mid,
        kind: media.kind,
        label: media.label,
        ref: media.ref,
        path: typeof media.ref === "string" && isRelative(media.ref) ? inRoot(media.ref) : undefined,
        ...(media.segments ? { segments: media.segments.map((seg) => seg.media) } : {}),
        ...(media.angles
          ? { angles: media.angles.map((ref) => ({ ref, path: typeof ref === "string" && isRelative(ref) ? inRoot(ref) : undefined })) }
          : {}),
      })),
      assets,
      ...(warnings.length ? { warnings } : {}),
    };
  }
}
