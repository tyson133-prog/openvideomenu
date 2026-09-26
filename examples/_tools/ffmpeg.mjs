// Helpers for example build scripts: run ffmpeg, find a font for drawtext, download files.
import { spawnSync } from "node:child_process";
import { createWriteStream, existsSync, renameSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { Readable } from "node:stream";
import { pipeline } from "node:stream/promises";

export function ffmpeg(args, { quiet = true } = {}) {
  const result = spawnSync("ffmpeg", ["-hide_banner", "-y", ...(quiet ? ["-loglevel", "error"] : []), ...args], {
    stdio: ["ignore", "inherit", "inherit"],
  });
  if (result.error) throw new Error(`Couldn't run ffmpeg (is it installed and on PATH?): ${result.error.message}`);
  if (result.status !== 0) throw new Error(`ffmpeg failed: ffmpeg ${args.join(" ")}`);
}

export function probeDuration(file) {
  const r = spawnSync("ffprobe", ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file], { encoding: "utf8" });
  const d = Number(r.stdout.trim());
  return Number.isFinite(d) ? d : undefined;
}

const FONT_CANDIDATES = [
  "C:/Windows/Fonts/georgia.ttf",
  "C:/Windows/Fonts/constan.ttf",
  "/System/Library/Fonts/Supplemental/Georgia.ttf",
  "/usr/share/fonts/truetype/dejavu/DejaVuSerif.ttf",
  "/usr/share/fonts/dejavu/DejaVuSerif.ttf",
  "/usr/share/fonts/TTF/DejaVuSerif.ttf",
];

export function findFont() {
  const font = FONT_CANDIDATES.find((f) => existsSync(f));
  if (!font) throw new Error(`No serif font found for drawtext; tried:\n  ${FONT_CANDIDATES.join("\n  ")}`);
  return font;
}

/** Escape a value for use inside a single-quoted ffmpeg filter option. */
export const filterQuote = (s) => `'${s.replace(/\\/g, "/").replace(/:/g, "\\:").replace(/'/g, "'\\''")}'`;

/** drawtext filter string. */
export function drawtext(font, text, { x, y, size, color = "white", shadow = true, extra = "" }) {
  const parts = [
    `fontfile=${filterQuote(font)}`,
    `text=${filterQuote(text)}`,
    `x=${x}`,
    `y=${y}`,
    `fontsize=${size}`,
    `fontcolor=${color}`,
  ];
  if (shadow) parts.push("shadowcolor=black@0.8", "shadowx=2", "shadowy=2");
  if (extra) parts.push(extra);
  return `drawtext=${parts.join(":")}`;
}

export async function download(url, file) {
  if (existsSync(file)) return false;
  process.stdout.write(`Downloading ${url}\n`);
  const response = await fetch(url);
  if (!response.ok || !response.body) throw new Error(`Download failed (${response.status}): ${url}`);
  const total = Number(response.headers.get("content-length")) || 0;
  let received = 0;
  let lastPct = -1;
  const body = Readable.fromWeb(response.body);
  body.on("data", (c) => {
    received += c.length;
    const pct = total ? Math.floor((received / total) * 100) : -1;
    if (pct !== lastPct && pct % 5 === 0) {
      process.stdout.write(`  ${pct}%\r`);
      lastPct = pct;
    }
  });
  await pipeline(body, createWriteStream(file + ".part"));
  renameSync(file + ".part", file);
  process.stdout.write("  done \n");
  return true;
}

/**
 * Keep media servers from treating menu art (motion loops, transitions, music) as titles:
 * Plex honours .plexignore (patterns, "*" = everything here), Jellyfin an .ignore file in the folder.
 */
export function writeIgnoreFiles(dir) {
  writeFileSync(join(dir, ".plexignore"), "# Menu art for a disc menu, not titles to list.\n*\n");
  writeFileSync(join(dir, ".ignore"), "");
}
