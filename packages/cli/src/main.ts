#!/usr/bin/env node
import { spawn } from "node:child_process";
import { readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { FORMAT_NAME, FORMAT_VERSION, parseManifest } from "@menustream/core";
import { findManifests } from "./find.js";
import { startPreview } from "./serve.js";

const USAGE = `${FORMAT_NAME} ${FORMAT_VERSION} (draft)

Usage:
  menustream validate <file-or-folder>     Check manifests; exits 1 when any has errors
  menustream preview <folder> [options]    Serve the folder and play its menus in a browser
      --port <n>     Port (default 8765)
      --host <addr>  Address to listen on (default 127.0.0.1; use 0.0.0.0 to test from a TV)
      --open         Open the browser
`;

function arg(args: string[], name: string): string | undefined {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
}

function validate(target: string): number {
  const isFile = statSync(target).isFile();
  const files = isFile ? [target] : findManifests(target).map((f) => join(target, f));
  if (files.length === 0) {
    console.error(`No manifests found in ${target}`);
    return 1;
  }
  let failed = 0;
  for (const file of files) {
    const result = parseManifest(readFileSync(file, "utf8"));
    console.log(`${result.ok ? "✓" : "✗"} ${file}`);
    for (const e of result.errors) console.log(`    error    ${e.path || "/"}  ${e.message}`);
    for (const w of result.warnings) console.log(`    warning  ${w.path || "/"}  ${w.message}`);
    if (!result.ok) failed++;
  }
  console.log(`\n${files.length - failed} of ${files.length} valid`);
  return failed > 0 ? 1 : 0;
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
    return command ? 0 : 1;
  }
  switch (command) {
    case "validate":
      return validate(target);
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
