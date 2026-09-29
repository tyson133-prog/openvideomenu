#!/usr/bin/env node
/**
 * Package the Jellyfin plugin for release: a zip of the plugin, the OpenVideoMenu library and
 * meta.json, plus the plugin-repository manifest Jellyfin reads when a user adds our repository
 * (Dashboard → Plugins → Repositories), so it can install and update the plugin itself.
 *
 *   node tools/package.mjs --version 0.1.2 --url https://…/open-video-menu_0.1.2.0.zip [--changelog "…"] [--out dist]
 *
 * Build the plugin first (dotnet build -c Release -p:Version=0.1.2). --url is where the zip will be
 * downloadable from once published (a release asset or the website); the repository manifest points
 * there and carries the zip's MD5, which Jellyfin checks.
 */
import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { mkdirSync, readFileSync, rmSync, writeFileSync, copyFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const arg = (name, fallback) => {
  const i = process.argv.indexOf(name);
  return i >= 0 && process.argv[i + 1] ? process.argv[i + 1] : fallback;
};
const here = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const project = join(here, "Jellyfin.Plugin.OpenVideoMenu");
const semver = arg("--version");
const url = arg("--url");
if (!semver || !url) throw new Error("usage: package.mjs --version X.Y.Z --url <where the zip will be> [--changelog text] [--out dir]");
const version = `${semver}.0`; // Jellyfin versions have four parts
const out = resolve(arg("--out", join(here, "dist")));
const bin = join(project, "bin", "Release", "net10.0");

const meta = JSON.parse(readFileSync(join(project, "meta.json"), "utf8"));
meta.version = version;
meta.changelog = arg("--changelog", meta.changelog);
meta.timestamp = new Date().toISOString();

// The zip: exactly the files Jellyfin loads, and meta.json.
const stage = join(out, "stage");
rmSync(stage, { recursive: true, force: true });
mkdirSync(stage, { recursive: true });
for (const dll of meta.assemblies) copyFileSync(join(bin, dll), join(stage, dll));
writeFileSync(join(stage, "meta.json"), JSON.stringify(meta, null, 2) + "\n");
const zipName = `open-video-menu_${version}.zip`;
const zip = join(out, zipName);
rmSync(zip, { force: true });
if (process.platform === "win32") {
  execFileSync("powershell.exe", ["-NoProfile", "-Command", `Compress-Archive -Path '${stage}\\*' -DestinationPath '${zip}'`]);
} else {
  execFileSync("zip", ["-j", "-q", zip, ...meta.assemblies.map((d) => join(stage, d)), join(stage, "meta.json")]);
}
rmSync(stage, { recursive: true, force: true });
const checksum = createHash("md5").update(readFileSync(zip)).digest("hex");

// The repository manifest: one plugin, this version (Jellyfin shows the newest it's compatible with).
const repository = [
  {
    guid: meta.guid,
    name: meta.name,
    description: meta.description,
    overview: meta.overview,
    owner: meta.owner,
    category: meta.category,
    versions: [{ version, changelog: meta.changelog, targetAbi: meta.targetAbi, sourceUrl: url, checksum, timestamp: meta.timestamp }],
  },
];
writeFileSync(join(out, "jellyfin-repository.json"), JSON.stringify(repository, null, 2) + "\n");
console.log(`✓ ${zip}\n✓ ${join(out, "jellyfin-repository.json")} (md5 ${checksum})`);
