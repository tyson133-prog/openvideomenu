// Channel art: the same gold ring-with-play mark as the Android TV launcher icon, on near-black.
import { mkdirSync } from "node:fs";
import { Image, hex } from "../../../examples/_tools/png.mjs";

const gold = hex("#FFD27A");
function art(w, h, markSize) {
  const img = new Image(w, h).paint(hex("#050505"), () => 1);
  const cx = w / 2, cy = h / 2, r = markSize / 2, stroke = markSize * 0.065;
  const smooth = (d) => Math.max(0, Math.min(1, 0.5 - d)); // 1px anti-aliasing
  img.paint(gold, (x, y) => smooth(Math.abs(Math.hypot(x - cx, y - cy) - r) - stroke / 2));
  // play triangle, optically centred like the Android icon
  const t = markSize * 0.38, left = cx - t * 0.32, top = cy - t / 2;
  img.paint(gold, (x, y) => {
    const u = (x - left) / (t * 0.76), v = (y - top) / t;
    return u >= 0 && u <= 1 && v >= u / 2 && v <= 1 - u / 2 ? 1 : 0;
  });
  return img;
}
mkdirSync(new URL("../images/", import.meta.url), { recursive: true });
art(540, 405, 260).save(new URL("../images/icon_fhd.png", import.meta.url).pathname.replace(/^\/([A-Z]:)/, "$1"));
art(290, 218, 140).save(new URL("../images/icon_hd.png", import.meta.url).pathname.replace(/^\/([A-Z]:)/, "$1"));
art(1920, 1080, 360).save(new URL("../images/splash_fhd.png", import.meta.url).pathname.replace(/^\/([A-Z]:)/, "$1"));
console.log("images written");
