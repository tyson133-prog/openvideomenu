import { closeSync, existsSync, openSync, readSync, statSync } from "node:fs";
import { dirname, extname, join } from "node:path";

const MP4_FAMILY = new Set([".mp4", ".m4a", ".m4v", ".mov"]);

/**
 * For MP4-family files: is the index ("moov") before the media data ("mdat")? Players that stream
 * over HTTP can then start at once; some (Android WebView) stall on files that need the end read
 * first. Walks only the top-level box headers, so it reads a few bytes even for large files.
 * Returns undefined for non-MP4 files or when neither box is found.
 */
export function isFaststart(file: string): boolean | undefined {
  if (!MP4_FAMILY.has(extname(file).toLowerCase())) return undefined;
  const size = statSync(file).size;
  const fd = openSync(file, "r");
  try {
    const header = Buffer.alloc(16);
    let offset = 0;
    for (let i = 0; i < 64 && offset + 8 <= size; i++) {
      readSync(fd, header, 0, 16, offset);
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
    closeSync(fd);
  }
}

/** Human-readable problems with a manifest's menu assets (paths are as the caller wants them shown). */
export function checkAssetFiles(assets: { shown: string; file: string }[]): string[] {
  const problems: string[] = [];
  for (const { shown, file } of assets) {
    if (!existsSync(file)) {
      problems.push(`Menu asset not found: ${shown}`);
      continue;
    }
    if (isFaststart(file) === false) {
      problems.push(
        `${shown} has its index at the end of the file, so some TV players stall on it. ` +
          `Fix: ffmpeg -i "${shown}" -c copy -movflags +faststart fixed${extname(file)}  (then replace the original)`,
      );
    }
  }
  return problems;
}

const MOTION = new Set([".mp4", ".m4v", ".mov", ".webm", ".mkv", ".m4a", ".mp3", ".aac", ".ogg", ".opus", ".flac", ".wav"]);

/**
 * Folders holding menu videos or music should tell media servers to skip them, or Plex/Jellyfin list
 * the clips as titles (or as extra versions of the film). Returns one warning per folder missing the
 * ignore files. [dirs] maps the folder as shown to its path on disk, for folders with menu assets.
 */
export function checkIgnoreFiles(assets: { shown: string; file: string }[]): string[] {
  const folders = new Map<string, string>(); // shown → disk
  for (const { shown, file } of assets)
    if (MOTION.has(extname(file).toLowerCase())) folders.set(dirname(shown), dirname(file));
  const problems: string[] = [];
  for (const [shown, disk] of folders) {
    const missing = [".plexignore", ".ignore"].filter((f) => !existsSync(join(disk, f)));
    if (missing.length)
      problems.push(
        `${shown || "."} holds menu video/music but no ${missing.join(" or ")}; Plex/Jellyfin may list those clips as titles. ` +
          `Add a .plexignore containing "*" and an empty .ignore to that folder.`,
      );
  }
  return problems;
}

