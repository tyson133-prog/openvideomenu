#!/usr/bin/env node
// Sets every npm package's version (packages/*), and their dependencies on each other, to one version: the
// release workflow runs it with the tag's version before packing. Only the version strings change; the files
// keep their layout.
//
//   node scripts/set-npm-version.mjs 1.0.0-preview.1
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const version = process.argv[2];
if (!/^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$/.test(version ?? "")) throw new Error("usage: set-npm-version.mjs <semver>");
const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const workspaces = JSON.parse(readFileSync(join(root, "package.json"), "utf8")).workspaces;
const files = workspaces.map((w) => join(root, w, "package.json"));
const names = files.map((f) => JSON.parse(readFileSync(f, "utf8")).name);
const quote = (s) => s.replace(/[.*+?^${}()|[\]\\/]/g, "\\$&");
for (const f of files) {
  let text = readFileSync(f, "utf8");
  // The package's own version: the first "version" key, at the top level.
  text = text.replace(/^(\s*"version":\s*)"[^"]*"/m, `$1"${version}"`);
  for (const name of names) text = text.replace(new RegExp(`("${quote(name)}":\\s*)"[^"]*"`, "g"), `$1"${version}"`);
  writeFileSync(f, text);
  const pkg = JSON.parse(text);
  if (pkg.version !== version) throw new Error(`${f}: version not set`);
  console.log(`${pkg.name} ${version}`);
}
