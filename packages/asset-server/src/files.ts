import { createReadStream, readdirSync, statSync } from "node:fs";
import type { IncomingMessage, ServerResponse } from "node:http";
import { extname, join, relative, sep } from "node:path";
import { MANIFEST_SUFFIX } from "@menustream/core";

export const CONTENT_TYPES: Record<string, string> = {
  ".json": "application/json; charset=utf-8",
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".map": "application/json",
  ".png": "image/png",
  ".jpg": "image/jpeg",
  ".jpeg": "image/jpeg",
  ".webp": "image/webp",
  ".gif": "image/gif",
  ".svg": "image/svg+xml",
  ".mp4": "video/mp4",
  ".m4v": "video/mp4",
  ".mov": "video/quicktime",
  ".webm": "video/webm",
  ".mkv": "video/x-matroska",
  ".ogv": "video/ogg",
  ".m3u8": "application/vnd.apple.mpegurl",
  ".mpd": "application/dash+xml",
  ".vtt": "text/vtt",
  ".mp3": "audio/mpeg",
  ".m4a": "audio/mp4",
  ".aac": "audio/aac",
  ".ogg": "audio/ogg",
  ".opus": "audio/ogg",
  ".flac": "audio/flac",
  ".wav": "audio/wav",
};

export const contentType = (file: string) => CONTENT_TYPES[extname(file).toLowerCase()] ?? "application/octet-stream";

/** Manifest files under `root` (or `root` itself when it is a manifest), as forward-slash relative paths. */
export function findManifests(root: string, maxDepth = 6): string[] {
  if (statSync(root).isFile()) return [root];
  const found: string[] = [];
  const walk = (dir: string, depth: number) => {
    let entries;
    try {
      entries = readdirSync(dir, { withFileTypes: true });
    } catch {
      return; // unreadable folder (permissions, vanished share): skip it
    }
    for (const entry of entries) {
      if (entry.name.startsWith(".") || entry.name === "node_modules" || entry.name === "@eaDir") continue;
      const full = join(dir, entry.name);
      if (entry.isDirectory() && depth < maxDepth) walk(full, depth + 1);
      else if (entry.isFile() && entry.name.toLowerCase().endsWith(MANIFEST_SUFFIX)) found.push(full);
    }
  };
  walk(root, 0);
  return found.sort().map((f) => relative(root, f).split(sep).join("/"));
}

/** Serve a file with HTTP Range support so seeking works. */
export function sendFile(req: IncomingMessage, res: ServerResponse, file: string, extraHeaders: Record<string, string> = {}) {
  const size = statSync(file).size;
  const range = /^bytes=(\d*)-(\d*)$/.exec(req.headers.range ?? "");
  const headers: Record<string, string | number> = {
    "Content-Type": contentType(file),
    "Accept-Ranges": "bytes",
    "Cache-Control": "no-cache",
    ...extraHeaders,
  };
  if (range && (range[1] || range[2])) {
    let start = range[1] ? Number(range[1]) : size - Number(range[2]);
    let end = range[1] && range[2] ? Number(range[2]) : size - 1;
    start = Math.max(0, start);
    end = Math.min(end, size - 1);
    if (start > end) {
      res.writeHead(416, { "Content-Range": `bytes */${size}` }).end();
      return;
    }
    res.writeHead(206, { ...headers, "Content-Range": `bytes ${start}-${end}/${size}`, "Content-Length": end - start + 1 });
    if (req.method === "HEAD") return void res.end();
    createReadStream(file, { start, end }).pipe(res);
  } else {
    res.writeHead(200, { ...headers, "Content-Length": size });
    if (req.method === "HEAD") return void res.end();
    createReadStream(file).pipe(res);
  }
}
