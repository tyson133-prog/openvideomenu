#!/usr/bin/env node
import { spawn } from "node:child_process";
import { readFileSync, statSync } from "node:fs";
import { basename, dirname, join } from "node:path";
import { BUNDLE_EXTENSION, BUNDLE_MANIFEST_NAME, FORMAT_NAME, FORMAT_VERSION, isBundleFileName, levelUsed, MANIFEST_SUFFIX, menuAssets, parseManifest } from "@openvideomenu/core";
import { checkAssetFiles, checkIgnoreFiles, findManifests, isFaststart, memberDataOffset, packBundle, readBundle, readMember, unpackBundle } from "@openvideomenu/asset-server";
import { startPreview } from "./serve.js";

const USAGE = `${FORMAT_NAME} ${FORMAT_VERSION} (preview)

Usage:
  openvideomenu validate <file-or-folder>     Check manifests and bundles; exits 1 when any has errors
  openvideomenu preview <folder> [options]    Serve the folder and play its menus in a browser
  openvideomenu pack <manifest-or-folder>     Pack each manifest and its menu art into <Title>.ovmz
  openvideomenu unpack <bundle.ovmz>          Unpack a bundle beside it (<Title>.openvideomenu.json + art)
      --out <path>   Where to write (pack: the bundle file; unpack: a folder)
      --port <n>     Port (default 8765)
      --host <addr>  Address to listen on (default 127.0.0.1; use 0.0.0.0 to test from a TV)
      --open         Open the browser
`;

function arg(args: string[], name: string): string | undefined {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
}

async function validate(target: string): Promise<number> {
  const isFile = statSync(target).isFile();
  const files = isFile ? [target] : findManifests(target).map((f) => join(target, f));
  if (files.length === 0) {
    console.error(`No manifests found in ${target}`);
    return 1;
  }
  let failed = 0;
  for (const file of files) {
    if (isBundleFileName(file)) {
      if (!(await validateBundle(file))) failed++;
      continue;
    }
    const result = parseManifest(readFileSync(file, "utf8"));
    console.log(`${result.ok ? "✓" : "✗"} ${file}${result.ok ? `  (Level ${levelUsed(result.manifest)})` : ""}`);
    for (const e of result.errors) console.log(`    error    ${e.path || "/"}  ${e.message}`);
    for (const w of result.warnings) console.log(`    warning  ${w.path || "/"}  ${w.message}`);
    if (result.ok) {
      // Menu art: present, and MP4/M4A files streamable from the start (TV players stall otherwise).
      const assets = menuAssets(result.manifest!).map((a) => ({ shown: join(dirname(file), a), file: join(dirname(file), ...a.split("/")) }));
      for (const problem of [...(await checkAssetFiles(assets)), ...(await checkIgnoreFiles(assets))]) console.log(`    warning  ${problem}`);
    }
    if (!result.ok) failed++;
  }
  console.log(`\n${files.length - failed} of ${files.length} valid`);
  return failed > 0 ? 1 : 0;
}

/** A bundle: stored entries, safe names, its manifest valid, and the members it names present. */
async function validateBundle(file: string): Promise<boolean> {
  let bundle;
  try {
    bundle = await readBundle(file);
  } catch (e) {
    console.log(`✗ ${file}\n    error    ${(e as Error).message}`);
    return false;
  }
  const manifest = bundle.members.get(BUNDLE_MANIFEST_NAME);
  const errors = [...bundle.errors, ...(manifest ? [] : [`No ${BUNDLE_MANIFEST_NAME} at the bundle's root.`])];
  const result = manifest && bundle.errors.length === 0 ? parseManifest((await readMember(file, manifest)).toString("utf8")) : undefined;
  const ok = errors.length === 0 && result?.ok === true;
  console.log(`${ok ? "✓" : "✗"} ${file}${ok ? `  (Level ${levelUsed(result!.manifest)})` : ""}`);
  for (const e of errors) console.log(`    error    ${e}`);
  for (const e of result?.errors ?? []) console.log(`    error    ${e.path || "/"}  ${e.message}`);
  for (const w of result?.warnings ?? []) console.log(`    warning  ${w.path || "/"}  ${w.message}`);
  if (ok) {
    for (const a of menuAssets(result.manifest!)) {
      const member = bundle.members.get(a);
      if (!member) {
        console.log(`    warning  Menu asset not in the bundle: ${a}`);
        continue;
      }
      const slice = { name: member.name, start: await memberDataOffset(file, member), size: member.size };
      if ((await isFaststart(file, slice)) === false) console.log(`    warning  ${a} has its index at the end of the file, so some TV players stall on it (ffmpeg -movflags +faststart, then pack again)`);
    }
  }
  return ok;
}

async function pack(target: string, out?: string): Promise<number> {
  const manifests = statSync(target).isFile() ? [target] : findManifests(target).filter((f) => !isBundleFileName(f)).map((f) => join(target, f));
  if (manifests.length === 0) {
    console.error(`No manifests to pack in ${target}`);
    return 1;
  }
  if (out && manifests.length > 1) {
    console.error("--out names one bundle; give a single manifest to use it.");
    return 1;
  }
  let failed = 0;
  for (const manifest of manifests) {
    const title = basename(manifest).slice(0, -MANIFEST_SUFFIX.length);
    const bundle = out ?? join(dirname(manifest), title + BUNDLE_EXTENSION);
    try {
      const { written, problems } = await packBundle(manifest, bundle);
      console.log(`✓ ${bundle}  (${written} files)`);
      for (const p of problems) console.log(`    warning  ${p}`);
    } catch (e) {
      console.log(`✗ ${manifest}\n    error    ${(e as Error).message}`);
      failed++;
    }
  }
  return failed > 0 ? 1 : 0;
}

async function unpack(bundle: string, out?: string): Promise<number> {
  const title = basename(bundle).slice(0, -BUNDLE_EXTENSION.length);
  const dir = out ?? dirname(bundle);
  const n = await unpackBundle(bundle, dir, title + MANIFEST_SUFFIX);
  console.log(`✓ ${join(dir, title + MANIFEST_SUFFIX)}  (${n} files)`);
  return 0;
}

function openBrowser(url: string) {
  const [cmd, args] =
    process.platform === "win32" ? ["cmd", ["/c", "start", "", url]] : process.platform === "darwin" ? ["open", [url]] : ["xdg-open", [url]];
  spawn(cmd, args as string[], { stdio: "ignore", detached: true }).unref();
}

async function main(argv: string[]): Promise<number> {
  const [command, target, ...rest] = argv;
  if (!command || command === "--help" || command === "-h" || !target) {
    console.log(USAGE);
    // Asking for help is a success; a command without its file or folder is a mistake.
    return command === "--help" || command === "-h" ? 0 : 1;
  }
  switch (command) {
    case "validate":
      return validate(target);
    case "pack":
      return pack(target, arg(rest, "--out"));
    case "unpack":
      return unpack(target, arg(rest, "--out"));
    case "preview": {
      const port = Number(arg(rest, "--port") ?? 8765);
      const host = arg(rest, "--host") ?? "127.0.0.1";
      const url = await startPreview({ root: target, port, host });
      const manifests = findManifests(target);
      console.log(`Previewing ${manifests.length} manifest(s) from ${target}`);
      console.log(`Open ${url}  (Ctrl+C to stop)`);
      if (rest.includes("--open")) openBrowser(url);
      return new Promise(() => {}); // run until interrupted
    }
    default:
      console.error(`Unknown command "${command}".\n`);
      console.log(USAGE);
      return 1;
  }
}

main(process.argv.slice(2)).then(
  (code) => process.exit(code),
  (e) => {
    console.error(e instanceof Error ? e.message : e);
    process.exit(1);
  },
);
