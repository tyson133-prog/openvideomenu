import { access, open } from "node:fs/promises";
import { dirname, extname, join } from "node:path";

const MP4_FAMILY = new Set([".mp4", ".m4a", ".m4v", ".mov"]);

/**
 * For MP4-family files: is the index ("moov") before the media data ("mdat")? Players that stream
 * over HTTP can then start at once; some (Android WebView) stall on files that need the end read
 * first. Walks only the top-level box headers, so it reads a few bytes even for large files.
 * Returns undefined for non-MP4 files or when neither box is found.
 */
export async function isFaststart(file: string, slice?: { name: string; start: number; size: number }): Promise<boolean | undefined> {
  if (!MP4_FAMILY.has(extname(slice?.name ?? file).toLowerCase())) return undefined;
  const fd = await open(file, "r");
  try {
    // A bundle member is a slice of the bundle file; box offsets count from its start.
    const base = slice?.start ?? 0;
    const size = slice?.size ?? (await fd.stat()).size;
    const header = Buffer.alloc(16);
    let offset = 0;
    for (let i = 0; i < 64 && offset + 8 <= size; i++) {
      await fd.read(header, 0, 16, base + offset);
      let boxSize = header.readUInt32BE(0);
      const type = header.toString("latin1", 4, 8);
      if (type === "moov") return true;
      if (type === "mdat") return false;
      if (boxSize === 1) boxSize = Number(header.readBigUInt64BE(8)); // 64-bit size
      else if (boxSize === 0) return undefined; // box runs to end of file
      if (boxSize < 8) return undefined; // corrupt
      offset += boxSize;
    }
    return undefined;
  } finally {
    await fd.close();
  }
}

const exists = (file: string) => access(file).then(
  () => true,
  () => false,
);

/** Run [fn] over [items] with at most [limit] at once (network shares answer many small requests slowly). */
export async function eachLimited<T, R>(items: T[], limit: number, fn: (item: T) => Promise<R>): Promise<R[]> {
  const results: R[] = new Array(items.length);
  let next = 0;
  const worker = async () => {
    while (next < items.length) {
      const i = next++;
      results[i] = await fn(items[i]!);
    }
  };
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, worker));
  return results;
}

/** Human-readable problems with a manifest's menu assets (paths are as the caller wants them shown). */
export async function checkAssetFiles(assets: { shown: string; file: string }[]): Promise<string[]> {
  const found = await eachLimited(assets, 16, async ({ shown, file }): Promise<string | undefined> => {
    if (!(await exists(file))) return `Menu asset not found: ${shown}`;
    if ((await isFaststart(file)) === false) {
      return (
        `${shown} has its index at the end of the file, so some TV players stall on it. ` +
        `Fix: ffmpeg -i "${shown}" -c copy -movflags +faststart fixed${extname(file)}  (then replace the original)`
      );
    }
    return undefined;
  });
  return found.filter((p): p is string => p !== undefined);
}

const MOTION = new Set([".mp4", ".m4v", ".mov", ".webm", ".mkv", ".m4a", ".mp3", ".aac", ".ogg", ".opus", ".flac", ".wav"]);

/**
 * Folders holding menu videos or music should tell media servers to skip them, or Plex/Jellyfin list
 * the clips as titles (or as extra versions of the film). Returns one warning per folder missing the
 * ignore files. [dirs] maps the folder as shown to its path on disk, for folders with menu assets.
 */
export async function checkIgnoreFiles(assets: { shown: string; file: string }[]): Promise<string[]> {
  const folders = new Map<string, string>(); // shown → disk
  for (const { shown, file } of assets)
    if (MOTION.has(extname(file).toLowerCase())) folders.set(dirname(shown), dirname(file));
  const problems: string[] = [];
  for (const [shown, disk] of folders) {
    const missing: string[] = [];
    for (const f of [".plexignore", ".ignore"]) if (!(await exists(join(disk, f)))) missing.push(f);
    if (missing.length)
      problems.push(
        `${shown || "."} holds menu video/music but no ${missing.join(" or ")}; Plex/Jellyfin may list those clips as titles. ` +
          `Add a .plexignore containing "*" and an empty .ignore to that folder.`,
      );
  }
  return problems;
}

