#!/usr/bin/env node
/**
 * Build the Roku app for release: without the conformance cases (dev-only test mode), as a zip,
 * and, with --sign, as a signed .pkg for the Roku Developer Dashboard (beta or public channel).
 *
 *   node tools/package.mjs [--version 0.1.2] [--sign] [--host 192.168.4.53]
 *
 * --version sets major.minor.build in the manifest copy that is packaged (the repo's manifest is
 * left alone). Roku requires every upload to have a higher version than the last.
 *
 * Signing happens on a Roku in developer mode, with the key made once by `genkey` (telnet <tv> 8080)
 * or loaded from an earlier .pkg with rekey. Keep that key and its password safe: updates to a
 * published channel must be signed with the same key.
 *   ROKU_DEV_PASSWORD      the TV's developer password (see deploy.mjs)
 *   ROKU_SIGNING_PASSWORD  the password genkey printed for the key
 * Both are only sent to the TV, never printed.
 */
import { execFileSync } from "node:child_process";
import { mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { rokuDeploy } from "roku-deploy";

const arg = (name, fallback) => {
  const i = process.argv.indexOf(name);
  return i >= 0 && process.argv[i + 1] ? process.argv[i + 1] : fallback;
};
const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const host = arg("--host", process.env.ROKU_HOST ?? "192.168.4.53");
const sign = process.argv.includes("--sign");

function secret(name) {
  if (process.env[name]) return process.env[name];
  if (process.platform === "win32") {
    const value = execFileSync("powershell.exe", ["-NoProfile", "-Command", `[Environment]::GetEnvironmentVariable('${name}','User')`], { encoding: "utf8" }).trim();
    if (value) return value;
  }
  throw new Error(`${name} isn't set.`);
}

// The manifest that ships, with the requested version.
let manifest = readFileSync(resolve(root, "manifest"), "utf8");
const version = arg("--version");
if (version) {
  const [major, minor, build] = version.split(".").map((n) => Number.parseInt(n, 10));
  if ([major, minor, build].some((n) => !Number.isInteger(n) || n < 0)) throw new Error("--version must be MAJOR.MINOR.BUILD");
  manifest = manifest
    .replace(/^major_version=.*$/m, `major_version=${major}`)
    .replace(/^minor_version=.*$/m, `minor_version=${minor}`)
    .replace(/^build_version=.*$/m, `build_version=${build}`);
}
const v = (key) => /^(?:major|minor|build)_version=(\d+)$/m.exec(manifest.split(/\r?\n/).find((l) => l.startsWith(key)) ?? "")?.[1];
const label = `${v("major_version")}.${v("minor_version")}.${v("build_version")}`;

const staging = resolve(root, "out", "release-src");
rmSync(staging, { recursive: true, force: true });
mkdirSync(staging, { recursive: true });
writeFileSync(resolve(staging, "manifest"), manifest);

// Compile check first, as deploy.mjs does.
execFileSync(process.execPath, [resolve(root, "node_modules/brighterscript/dist/cli.js"), "--create-package", "false"], { cwd: root, stdio: "pipe" });

const options = {
  host,
  rootDir: root,
  // Release contents: everything but the conformance cases; the manifest comes from staging.
  files: ["source/**/*", "components/**/*", "images/**/*", { src: resolve(staging, "manifest"), dest: "" }],
  outDir: resolve(root, "out"),
  outFile: `ovmplayer-roku-${label}`,
  deleteInstalledChannel: false,
};

if (!sign) {
  await rokuDeploy.createPackage(options);
  console.log(`✓ out/ovmplayer-roku-${label}.zip (unsigned; add --sign for a .pkg)`);
} else {
  const pkg = await rokuDeploy.deployAndSignPackage({ ...options, password: secret("ROKU_DEV_PASSWORD"), signingPassword: secret("ROKU_SIGNING_PASSWORD") });
  console.log(`✓ ${pkg} (signed on ${host})`);
}
