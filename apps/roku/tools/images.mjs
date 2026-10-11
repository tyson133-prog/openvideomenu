// Channel art: the same mark as the Android TV launcher icon (a ring in a foil sweep, a play mark and
// "OVM"), on deep navy. Drawn in the launcher icon's 108-unit space, centred on (54, 54); the ring is
// 54 units across.
import { mkdirSync } from "node:fs";
import { Image, hex } from "../../../examples/_tools/png.mjs";

const mix = (a, b, t) => a.map((c, i) => c + (b[i] - c) * t);
/** A colour along gradient stops [[offset, "#rrggbb"], ...] at t in 0..1. */
function along(stops, t) {
  const s = stops.map(([o, c]) => [o, hex(c)]);
  if (t <= s[0][0]) return s[0][1];
  for (let i = 1; i < s.length; i++) if (t <= s[i][0]) return mix(s[i - 1][1], s[i][1], (t - s[i - 1][0]) / (s[i][0] - s[i - 1][0]));
  return s[s.length - 1][1];
}
// From the top, clockwise.
const sweep = [[0, "#23BEFF"], [1 / 6, "#3B85F3"], [2 / 6, "#8F6CE8"], [3 / 6, "#F0A3FA"], [4 / 6, "#D2F093"], [5 / 6, "#7FEDDA"], [1, "#23BEFF"]];
const playStops = [[0, "#7FEDDA"], [0.45, "#23BEFF"], [1, "#8F6CE8"]];
const letterStops = [[0, "#23BEFF"], [0.5, "#3B85F3"], [1, "#8F6CE8"]];

const segment = (px, py, ax, ay, bx, by) => {
  const dx = bx - ax, dy = by - ay;
  const t = Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)));
  return Math.hypot(px - ax - t * dx, py - ay - t * dy);
};
const polyline = (pts) => pts.slice(1).map((p, i) => [...pts[i], ...p]);
// O, V and M as 1.5-unit strokes with round ends.
const strokes = [...polyline([[50, 63.5], [53.5, 70.5], [57, 63.5]]), ...polyline([[59, 70.5], [59, 63.5], [62.5, 68], [66, 63.5], [66, 70.5]])];
const play = [[48, 38], [64.5, 48], [48, 58]];

function art(w, h, markSize) {
  const img = new Image(w, h);
  const far = Math.hypot(w, h) * 1.05;
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    img.blend(x, y, mix(hex("#14244A"), hex("#000517"), Math.min(1, Math.hypot(x + 0.5, y + 0.5) / far)), 1);
  }
  const k = markSize / 54, cx = w / 2, cy = h / 2, px = 1 / k; // px: one pixel in units
  const cover = (d) => Math.max(0, Math.min(1, 0.5 - d / px));
  const reach = (30 / 27) * (markSize / 2) + 2; // the ring and its stroke, with room for anti-aliasing
  const x0 = Math.floor(cx - reach), x1 = Math.ceil(cx + reach);
  const y0 = Math.floor(cy - reach), y1 = Math.ceil(cy + reach);
  for (let y = y0; y < y1; y++) for (let x = x0; x < x1; x++) {
    const u = (x + 0.5 - cx) / k + 54, v = (y + 0.5 - cy) / k + 54;
    const ring = Math.abs(Math.hypot(u - 54, v - 54) - 27) - 1.75;
    const turn = (Math.atan2(u - 54, 54 - v) / (2 * Math.PI) + 1) % 1;
    img.blend(x, y, along(sweep, turn), cover(ring));
    // play mark: inside all three edges
    let d = -Infinity;
    for (let i = 0; i < 3; i++) {
      const [ax, ay] = play[i], [bx, by] = play[(i + 1) % 3];
      const len = Math.hypot(bx - ax, by - ay);
      d = Math.max(d, ((u - ax) * (by - ay) - (v - ay) * (bx - ax)) / len);
    }
    img.blend(x, y, along(playStops, ((u - 48) / 16.5 + (v - 38) / 20) / 2), cover(d));
    const letters = Math.min(Math.abs(Math.hypot(u - 45.5, v - 67) - 3.5), ...strokes.map((s) => segment(u, v, ...s))) - 0.75;
    img.blend(x, y, along(letterStops, (u - 42) / 24), cover(letters));
  }
  return img;
}
mkdirSync(new URL("../images/", import.meta.url), { recursive: true });
art(540, 405, 260).save(new URL("../images/icon_fhd.png", import.meta.url).pathname.replace(/^\/([A-Z]:)/, "$1"));
art(290, 218, 140).save(new URL("../images/icon_hd.png", import.meta.url).pathname.replace(/^\/([A-Z]:)/, "$1"));
art(1920, 1080, 360).save(new URL("../images/splash_fhd.png", import.meta.url).pathname.replace(/^\/([A-Z]:)/, "$1"));
art(1280, 720, 240).save(new URL("../images/splash_hd.png", import.meta.url).pathname.replace(/^\/([A-Z]:)/, "$1"));
console.log("images written");
