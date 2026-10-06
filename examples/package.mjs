#!/usr/bin/env node
// Zips the built examples as a library: Movies/Sintel (2010) and TV/Example Show, ready to copy into Plex or
// Jellyfin libraries. Build them first (sintel with --film, so the real film is in it). Needs 7-Zip (7z).
//
//   node examples/package.mjs [--out dist/openvideomenu-examples.zip]

import { execFileSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const i = process.argv.indexOf("--out");
const out = resolve(i >= 0 ? process.argv[i + 1] : join(here, "..", "dist", "openvideomenu-examples.zip"));
const stage = join(here, "_cache", "package");

const titles = [
  {
    from: join(here, "sintel"),
    to: join("Movies", "Sintel (2010)"),
    files: ["Sintel (2010).mp4", "Sintel (2010).openvideomenu.json", "menus", "Trailers", "Other"],
  },
  {
    from: join(here, "dvd-shaped"),
    to: join("TV", "Example Show"),
    files: ["Example Show.openvideomenu.json", "menus", "Season 00", "Season 01"],
  },
];

if (existsSync(join(here, "_cache", "sintel", "feature-is-stand-in"))) throw new Error("Sintel was built with a stand-in for the film: build it with --film.");
rmSync(stage, { recursive: true, force: true });
for (const t of titles) {
  for (const f of t.files) {
    const src = join(t.from, f);
    if (!existsSync(src)) throw new Error(`Missing ${src}: build the examples first.`);
    cpSync(src, join(stage, t.to, f), { recursive: true });
  }
}
writeFileSync(join(stage, "README.txt"), [
  "Open Video Menu examples: https://openvideomenu.org",
  "",
  "Copy Movies/Sintel (2010) into a movie library and TV/Example Show into a TV library. Players find the",
  "menus through the Jellyfin plugin or, for Plex, the menu server.",
  "",
  "Sintel (c) copyright Blender Foundation | durian.blender.org, under Creative Commons Attribution 3.0.",
  "The Example Show was generated for the project by examples/dvd-shaped/build.mjs.",
  "",
].join("\r\n"));

mkdirSync(dirname(out), { recursive: true });
rmSync(out, { force: true });
// Stored, not compressed: the video and menu art are compressed already.
execFileSync("7z", ["a", "-tzip", "-mx0", out, "."], { cwd: stage, stdio: ["ignore", "ignore", "inherit"] });
rmSync(stage, { recursive: true, force: true });
console.log(`${out}`);
