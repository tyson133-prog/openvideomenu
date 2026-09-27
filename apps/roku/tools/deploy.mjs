#!/usr/bin/env node
/**
 * Build and sideload the Roku app onto a TV in developer mode.
 *
 *   node tools/deploy.mjs [--host 192.168.4.53] [--conformance]
 *
 * The dev password comes from ROKU_DEV_PASSWORD: the environment, or (on Windows) the user
 * environment set with `setx`, read from the registry so a restart isn't needed. It is only sent
 * to the TV's installer and never printed.
 *
 * --conformance also launches the app in test mode (conformance=1) and prints the conformance
 * results from the TV's debug console (port 8085). Exit code 1 if any case fails.
 */
import { execFileSync } from "node:child_process";
import net from "node:net";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import { rokuDeploy } from "roku-deploy";

const arg = (name, fallback) => {
  const i = process.argv.indexOf(name);
  return i >= 0 && process.argv[i + 1] ? process.argv[i + 1] : fallback;
};
const host = arg("--host", process.env.ROKU_HOST ?? "192.168.4.53");
const conformance = process.argv.includes("--conformance");
const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");

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
  throw new Error("ROKU_DEV_PASSWORD isn't set. Run: setx ROKU_DEV_PASSWORD \"<your dev password>\"");
}

const ecp = (path) => fetch(`http://${host}:8060${path}`, { method: "POST" });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** Collect debug-console lines until `done` matches one, or `timeoutMs` passes. */
function readConsole(done, timeoutMs) {
  return new Promise((resolveLines, reject) => {
    const lines = [];
    let buffer = "";
    const socket = net.connect(8085, host);
    const finish = () => {
      socket.destroy();
      resolveLines(lines);
    };
    const timer = setTimeout(finish, timeoutMs);
    socket.on("data", (d) => {
      buffer += d.toString("utf8");
      const parts = buffer.split(/\r?\n/);
      buffer = parts.pop() ?? "";
      for (const line of parts) {
        lines.push(line);
        if (done(line)) {
          clearTimeout(timer);
          finish();
        }
      }
    });
    socket.on("error", (e) => {
      clearTimeout(timer);
      reject(e);
    });
  });
}

const files = [
  "manifest",
  "source/**/*",
  "components/**/*",
  "images/**/*",
  // Conformance cases travel with dev builds so the TV can check the navigator against them.
  { src: "../../conformance/cases/*.json", dest: "conformance" },
];

console.log(`Deploying to ${host}…`);
await rokuDeploy.deploy({ host, password: devPassword(), rootDir: root, files, outDir: resolve(root, "out"), outFile: "menustream-roku" });
console.log("Installed.");

if (conformance) {
  await ecp("/keypress/Home");
  await sleep(2000);
  // A runtime error stops the app in the debugger; show it instead of waiting for a result.
  const failure = /runtime error|BRIGHTSCRIPT: ERROR|Brightscript Debugger>/i;
  const reading = readConsole((l) => l.includes("MSCONF DONE") || /Brightscript Debugger>/i.test(l), 60_000);
  await sleep(500);
  await ecp("/launch/dev?conformance=1");
  const lines = await reading;
  const results = lines.filter((l) => l.includes("MSCONF") || failure.test(l) || /^\s*file\/line:/.test(l));
  for (const l of results) console.log(l.trim());
  const done = results.find((l) => l.includes("MSCONF DONE"));
  if (!done) {
    console.error("No result from the TV (see the debug console: telnet " + host + " 8085).");
    process.exit(1);
  }
  process.exit(/failed=0\b/.test(done) ? 0 : 1);
}
