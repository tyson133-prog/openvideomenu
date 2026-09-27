#!/usr/bin/env node
/**
 * Save a screenshot of the dev app on the Roku (developer mode's own screenshot feature).
 *   node tools/screenshot.mjs <out.jpg> [--host 192.168.4.53]
 * Uses ROKU_DEV_PASSWORD like deploy.mjs; the password is only sent to the TV.
 */
import { execFileSync } from "node:child_process";
import { copyFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { rokuDeploy } from "roku-deploy";

const arg = (name, fallback) => {
  const i = process.argv.indexOf(name);
  return i >= 0 && process.argv[i + 1] ? process.argv[i + 1] : fallback;
};
const host = arg("--host", process.env.ROKU_HOST ?? "192.168.4.53");
const out = process.argv[2] && !process.argv[2].startsWith("--") ? resolve(process.argv[2]) : resolve("roku-screenshot.jpg");

function devPassword() {
  if (process.env.ROKU_DEV_PASSWORD) return process.env.ROKU_DEV_PASSWORD;
  if (process.platform === "win32") {
    const value = execFileSync(
      "powershell.exe",
      ["-NoProfile", "-Command", "[Environment]::GetEnvironmentVariable('ROKU_DEV_PASSWORD','User')"],
      { encoding: "utf8" },
    ).trim();
    if (value) return value;
  }
  throw new Error("ROKU_DEV_PASSWORD isn't set.");
}

const saved = await rokuDeploy.takeScreenshot({
  host,
  password: devPassword(),
  outDir: resolve(dirname(fileURLToPath(import.meta.url)), "../out"),
});
copyFileSync(saved, out);
console.log(out);
