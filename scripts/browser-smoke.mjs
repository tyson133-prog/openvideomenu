#!/usr/bin/env node
// Drives a real headless Chrome/Edge through a preview page over the DevTools protocol: presses
// remote-control keys, checks the navigator state after each step and saves screenshots.
//
//   node scripts/browser-smoke.mjs <url> <steps.json> [outDir]
//
// steps.json: [{ "key": "ArrowDown" | "Enter" | "Escape" | ..., "wait": ms, "expect": {mode, menu, focus},
//               "shot": "name", "eval": "js expression to print" }, ...]
import { spawn } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const [url, stepsFile, outDir = "smoke-out", initScript] = process.argv.slice(2);
if (!url || !stepsFile) {
  console.error("usage: browser-smoke.mjs <url> <steps.json> [outDir]");
  process.exit(2);
}
const steps = JSON.parse(readFileSync(stepsFile, "utf8"));
mkdirSync(outDir, { recursive: true });

const BROWSERS = [
  process.env.CHROME_PATH,
  "C:/Program Files/Google/Chrome/Application/chrome.exe",
  "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
  "/usr/bin/google-chrome",
  "/usr/bin/chromium",
].filter(Boolean);
const browserPath = BROWSERS.find((b) => existsSync(b));
if (!browserPath) throw new Error("No Chrome/Edge found; set CHROME_PATH");

const port = 9300 + Math.floor(Math.random() * 500);
const profile = mkdtempSync(join(tmpdir(), "ms-smoke-"));
const browser = spawn(browserPath, [
  "--headless=new", `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, "--window-size=1280,720",
  "--autoplay-policy=no-user-gesture-required", "--mute-audio", "--no-first-run", "about:blank",
], { stdio: "ignore" });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let target;
for (let i = 0; i < 50 && !target; i++) {
  await sleep(200);
  try {
    const list = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
    target = list.find((t) => t.type === "page");
  } catch {}
}
if (!target) throw new Error("Browser didn't start");

const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((r) => ws.addEventListener("open", r, { once: true }));
let nextId = 1;
const pending = new Map();
const logs = [];
ws.addEventListener("message", (e) => {
  const msg = JSON.parse(e.data);
  if (msg.id && pending.has(msg.id)) {
    pending.get(msg.id)(msg);
    pending.delete(msg.id);
  } else if (msg.method === "Runtime.consoleAPICalled") {
    logs.push(`${msg.params.type}: ${msg.params.args.map((a) => a.value ?? a.description).join(" ")}`);
  } else if (msg.method === "Runtime.exceptionThrown") {
    logs.push(`exception: ${msg.params.exceptionDetails.exception?.description ?? msg.params.exceptionDetails.text}`);
  }
});
const send = (method, params = {}) =>
  new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, (m) => (m.error ? reject(new Error(`${method}: ${m.error.message}`)) : resolve(m.result)));
    ws.send(JSON.stringify({ id, method, params }));
  });
const evaluate = async (expression) =>
  (await send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true })).result.value;

const KEYS = {
  ArrowUp: 38, ArrowDown: 40, ArrowLeft: 37, ArrowRight: 39, Enter: 13, Escape: 27, Backspace: 8, " ": 32,
  m: 77, t: 84, // the Menu and Title keys (Level 2)
};
async function press(key) {
  const code = KEYS[key];
  const base = { key, code: key === " " ? "Space" : key, windowsVirtualKeyCode: code, nativeVirtualKeyCode: code };
  await send("Input.dispatchKeyEvent", { type: "rawKeyDown", ...base });
  if (key === "Enter") await send("Input.dispatchKeyEvent", { type: "char", text: "\r", ...base });
  await send("Input.dispatchKeyEvent", { type: "keyUp", ...base });
}

await send("Runtime.enable");
await send("Page.enable");
await send("Emulation.setDeviceMetricsOverride", { width: 1280, height: 720, deviceScaleFactor: 1, mobile: false });
if (initScript) await send("Page.addScriptToEvaluateOnNewDocument", { source: readFileSync(initScript, "utf8") });
await send("Page.navigate", { url });
await sleep(1500);

const stateExpr = `(async () => { const p = await window.menuPlayer; if (!p) return null;
  const s = p.navState; const v = document.querySelector("video.ms-video");
  return { mode: s.mode, menu: s.menu, focus: s.focus, buttonsReady: s.buttonsReady, intro: s.intro, overlay: s.overlay ? s.overlay.cue + "/" + s.overlay.focus : null, video: v && !v.hidden ? { src: decodeURI(v.src.split("/media/")[1] || ""), t: Math.round(v.currentTime * 10) / 10, paused: v.paused } : null }; })()`;

let failures = 0;
for (const [i, step] of steps.entries()) {
  if (step.key) await press(step.key);
  if (step.js) await evaluate(step.js);
  await sleep(step.wait ?? 400);
  const state = await evaluate(stateExpr);
  let ok = true;
  for (const [k, v] of Object.entries(step.expect ?? {})) {
    const actual = k === "videoSrc" ? state?.video?.src : state?.[k];
    if (k === "minTime" ? !(state?.video?.t >= v) : actual !== v) ok = false;
  }
  if (!ok) failures++;
  const extra = step.eval ? ` ${JSON.stringify(await evaluate(step.eval))}` : "";
  console.log(`${ok ? "ok  " : "FAIL"} ${i} ${step.key ?? step.js ?? "(wait)"} -> ${JSON.stringify(state)}${ok ? "" : ` expected ${JSON.stringify(step.expect)}`}${extra}`);
  if (step.shot) {
    const { data } = await send("Page.captureScreenshot", { format: "png" });
    writeFileSync(join(outDir, `${step.shot}.png`), Buffer.from(data, "base64"));
  }
}
if (logs.length) console.log("\nconsole:\n  " + logs.join("\n  "));
ws.close();
browser.kill();
console.log(failures ? `\n${failures} step(s) failed` : "\nall steps passed");
process.exit(failures ? 1 : 0);
