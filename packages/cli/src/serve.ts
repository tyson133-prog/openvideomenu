import { createReadStream, existsSync, readFileSync, statSync } from "node:fs";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { createRequire } from "node:module";
import { extname, resolve, sep } from "node:path";
import { FORMAT_NAME } from "@menustream/core";
import { findManifests } from "./find.js";

const CONTENT_TYPES: Record<string, string> = {
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

export interface PreviewOptions {
  root: string;
  port: number;
  host: string;
}

function playerBundlePath(): string {
  const require = createRequire(import.meta.url);
  return require.resolve("@menustream/renderer-web/bundle");
}

const escapeHtml = (s: string) =>
  s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]!);

function indexPage(manifests: string[]): string {
  const items = manifests
    .map((m) => `<li><a href="/play?m=${encodeURIComponent(m)}">${escapeHtml(m)}</a> · <a href="/play?m=${encodeURIComponent(m)}&view=fallback">list view</a></li>`)
    .join("\n");
  return `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>${FORMAT_NAME} preview</title>
<style>body{font:18px system-ui,sans-serif;background:#111;color:#eee;padding:2em}a{color:#ffd54a}li{margin:.4em 0}</style></head>
<body><h1>${FORMAT_NAME} preview</h1>${manifests.length ? `<ul>${items}</ul>` : "<p>No manifests found.</p>"}</body></html>`;
}

function playerPage(): string {
  return `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>${FORMAT_NAME} preview</title>
<style>html,body{margin:0;height:100%;background:#000}#player{position:fixed;inset:0}
#help{position:fixed;right:12px;bottom:10px;z-index:50;font:13px system-ui,sans-serif;color:#aaa;background:rgba(0,0,0,.6);padding:6px 10px;border-radius:6px;transition:opacity 1s}</style>
</head><body><div id="player"></div>
<div id="help">Arrows move · Enter selects · Esc/Backspace goes back or stops · <a style="color:#ffd54a" id="toggle" href="#">list view</a></div>
<script src="/_player/menustream-player.js"></script>
<script>
  const params = new URLSearchParams(location.search);
  const m = params.get("m");
  const view = params.get("view") === "fallback" ? "fallback" : "menu";
  const toggle = document.getElementById("toggle");
  const other = new URLSearchParams(params); other.set("view", view === "fallback" ? "menu" : "fallback");
  toggle.href = "?" + other.toString();
  toggle.textContent = view === "fallback" ? "menu view" : "list view";
  setTimeout(() => (document.getElementById("help").style.opacity = "0.25"), 5000);
  window.menuPlayer = MenuStreamPlayer.loadPlayer(document.getElementById("player"), "/media/" + m.split("/").map(encodeURIComponent).join("/"), {
    view,
    showHidden: params.has("hidden"),
    onExit: () => { if (history.length > 1) history.back(); },
  });
</script></body></html>`;
}

/** Serve a file with HTTP Range support so video seeking (chapters, resume) works. */
function sendFile(req: IncomingMessage, res: ServerResponse, file: string) {
  const size = statSync(file).size;
  const type = CONTENT_TYPES[extname(file).toLowerCase()] ?? "application/octet-stream";
  const range = /^bytes=(\d*)-(\d*)$/.exec(req.headers.range ?? "");
  const headers: Record<string, string | number> = { "Content-Type": type, "Accept-Ranges": "bytes", "Cache-Control": "no-cache" };
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

export function startPreview({ root, port, host }: PreviewOptions): Promise<string> {
  const rootDir = resolve(root);
  const bundle = playerBundlePath();

  const server = createServer((req, res) => {
    try {
      const url = new URL(req.url ?? "/", "http://localhost");
      const path = decodeURIComponent(url.pathname);
      if (path === "/") {
        const manifests = findManifests(rootDir);
        if (manifests.length === 1) return void res.writeHead(302, { Location: `/play?m=${encodeURIComponent(manifests[0]!)}` }).end();
        return void res.writeHead(200, { "Content-Type": CONTENT_TYPES[".html"]! }).end(indexPage(manifests));
      }
      if (path === "/play") return void res.writeHead(200, { "Content-Type": CONTENT_TYPES[".html"]! }).end(playerPage());
      if (path === "/_player/menustream-player.js") return sendFile(req, res, bundle);
      if (path === "/_player/menustream-player.js.map" && existsSync(bundle + ".map")) return sendFile(req, res, bundle + ".map");
      if (path.startsWith("/media/")) {
        const file = resolve(rootDir, "." + path.slice("/media".length));
        if (file !== rootDir && !file.startsWith(rootDir + sep)) return void res.writeHead(403).end("Forbidden");
        if (!existsSync(file) || !statSync(file).isFile()) return void res.writeHead(404).end("Not found");
        return sendFile(req, res, file);
      }
      res.writeHead(404).end("Not found");
    } catch (e) {
      res.writeHead(500).end(String(e));
    }
  });

  return new Promise((resolvePromise, reject) => {
    server.once("error", reject);
    server.listen(port, host, () => resolvePromise(`http://${host === "0.0.0.0" ? "localhost" : host}:${port}/`));
  });
}

export function readText(file: string): string {
  return readFileSync(file, "utf8");
}
