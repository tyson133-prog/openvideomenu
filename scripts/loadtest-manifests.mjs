#!/usr/bin/env node
/**
 * Load test: many Matrix-sized manifests (about 50 menus and 450 menu assets each) for timing the
 * asset server's scan and index, and the TV app's Disc Menus row.
 *
 *   node scripts/loadtest-manifests.mjs [--count 400] [--movies <folder>] [--out <folder>]
 *
 * With --movies (e.g. //mediacenter/MP4/Movies, only listed, never written), the first manifests
 * point at those real films so the TV app's Plex lookups are real; the rest point at films that
 * don't exist (the worst case: the app searches TV shows for them too). Output goes to
 * examples/_cache/loadtest/ (git-ignored); add it as an extra --media root on the asset server.
 */
import { mkdirSync, readdirSync, rmSync, statSync, writeFileSync } from "node:fs";
import { extname, join, resolve } from "node:path";
import { flatFill, hex } from "../examples/_tools/png.mjs";

const arg = (name, fallback) => {
  const i = process.argv.indexOf(name);
  return i >= 0 && process.argv[i + 1] ? process.argv[i + 1] : fallback;
};
const count = Number(arg("--count", 400));
const moviesDir = arg("--movies", undefined);
const out = resolve(arg("--out", "examples/_cache/loadtest"));
const MENUS = 50;
const BUTTONS = 4; // per menu; each has selected + activated art → 50 × (1 + 4 × 2) ≈ 450 assets
const VIDEO = new Set([".mp4", ".mkv", ".m4v", ".avi", ".mov"]);

rmSync(out, { recursive: true, force: true });

// Shared menu art: every manifest names its own paths, but they resolve into one folder.
const art = join(out, "_menus");
mkdirSync(art, { recursive: true });
flatFill(64, 36, hex("#223344")).save(join(art, "bg.png"));
flatFill(16, 16, hex("#ffcc00")).save(join(art, "sel.png"));
flatFill(16, 16, hex("#ff6600")).save(join(art, "act.png"));
const assetNames = [];
for (let m = 0; m < MENUS; m++) {
  const names = { bg: `m${m}-bg.png`, buttons: [] };
  flatFill(64, 36, hex("#223344")).save(join(art, names.bg));
  for (let b = 0; b < BUTTONS; b++) {
    const sel = `m${m}-b${b}-sel.png`;
    const act = `m${m}-b${b}-act.png`;
    flatFill(16, 16, hex("#ffcc00")).save(join(art, sel));
    flatFill(16, 16, hex("#ff6600")).save(join(art, act));
    names.buttons.push({ sel, act });
  }
  assetNames.push(names);
}

/** Real films: folder name and its main video file. */
function realFilms() {
  if (!moviesDir) return [];
  const films = [];
  for (const folder of readdirSync(moviesDir).sort()) {
    const dir = join(moviesDir, folder);
    let files;
    try {
      if (!statSync(dir).isDirectory()) continue;
      files = readdirSync(dir).filter((f) => VIDEO.has(extname(f).toLowerCase()));
    } catch {
      continue;
    }
    // The largest-looking main file: prefer one named like the folder, else the first.
    const main = files.find((f) => f.toLowerCase().startsWith(folder.toLowerCase().replace(/ \(\d{4}\)$/, ""))) ?? files[0];
    if (main) films.push({ folder, file: main, title: folder.replace(/ \(\d{4}\)$/, "") });
  }
  return films;
}

const real = realFilms();
let written = 0;
for (let i = 0; i < count; i++) {
  const film = real[i] ?? { folder: `Load Test Film ${String(i).padStart(3, "0")} (2000)`, title: `Load Test Film ${String(i).padStart(3, "0")}` };
  film.file ??= `${film.folder}.mp4`;
  const dir = join(out, "Movies", film.folder);
  mkdirSync(dir, { recursive: true });
  const shared = "../../_menus/";
  const menus = {};
  for (let m = 0; m < MENUS; m++) {
    const n = assetNames[m];
    menus[`menu${m}`] = {
      canvas: { width: 720, height: 480, aspect: "16:9" },
      background: { image: shared + n.bg },
      buttons: n.buttons.map((b, j) => ({
        id: `b${j}`,
        rect: [100 + j * 120, 380, 100, 40],
        label: `Button ${j}`,
        states: { selected: { image: shared + b.sel }, activated: { image: shared + b.act } },
        nav: { left: j > 0 ? `b${j - 1}` : undefined, right: j < BUTTONS - 1 ? `b${j + 1}` : undefined },
        action: j === 0 ? { type: "play", media: "feature" } : { type: "goto", menu: `menu${(m + j) % MENUS}` },
      })),
    };
  }
  const manifest = {
    format: "menustream",
    version: "0.2",
    title: { name: film.title },
    source: { kind: "loadtest", generator: "scripts/loadtest-manifests.mjs" },
    media: { feature: { ref: film.file, kind: "feature", label: film.title } },
    menus,
    rootMenu: "menu0",
    fallback: [{ label: "Movie", items: [{ label: "Play Movie", action: { type: "play", media: "feature" } }] }],
  };
  writeFileSync(join(dir, `${film.title}.menustream.json`), JSON.stringify(manifest, null, 2));
  written++;
}
console.log(`Wrote ${written} manifests (${Math.min(real.length, count)} for real films) to ${out}`);
