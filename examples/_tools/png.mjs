// Minimal RGBA PNG writer and shape helpers for generating menu highlight overlays.
// No dependencies (uses node:zlib), so example builds run anywhere Node and ffmpeg exist.
import { writeFileSync } from "node:fs";
import { deflateSync } from "node:zlib";

const CRC_TABLE = new Uint32Array(256).map((_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});

function crc32(buf) {
  let c = 0xffffffff;
  for (const b of buf) c = CRC_TABLE[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const out = Buffer.alloc(12 + data.length);
  out.writeUInt32BE(data.length, 0);
  out.write(type, 4, "ascii");
  data.copy(out, 8);
  out.writeUInt32BE(crc32(out.subarray(4, 8 + data.length)), 8 + data.length);
  return out;
}

export class Image {
  constructor(width, height) {
    this.width = width;
    this.height = height;
    this.data = new Float32Array(width * height * 4); // premultiplied-free RGBA, 0..1
  }

  /** Composite a colour with coverage `a` (0..1) over pixel (x, y). */
  blend(x, y, [r, g, b, alpha = 1], a) {
    if (x < 0 || y < 0 || x >= this.width || y >= this.height || a <= 0) return;
    const i = (y * this.width + x) * 4;
    const sa = Math.min(1, a * alpha);
    const da = this.data[i + 3];
    const oa = sa + da * (1 - sa);
    if (oa === 0) return;
    for (let c = 0; c < 3; c++) this.data[i + c] = ([r, g, b][c] * sa + this.data[i + c] * da * (1 - sa)) / oa;
    this.data[i + 3] = oa;
  }

  /** Paint every pixel with coverage from fn(x, y) in 0..1. */
  paint(color, fn) {
    for (let y = 0; y < this.height; y++) for (let x = 0; x < this.width; x++) this.blend(x, y, color, fn(x + 0.5, y + 0.5));
    return this;
  }

  save(file) {
    const raw = Buffer.alloc((this.width * 4 + 1) * this.height);
    for (let y = 0; y < this.height; y++) {
      raw[y * (this.width * 4 + 1)] = 0; // filter: none
      for (let x = 0; x < this.width; x++) {
        const i = (y * this.width + x) * 4;
        const o = y * (this.width * 4 + 1) + 1 + x * 4;
        for (let c = 0; c < 4; c++) raw[o + c] = Math.round(Math.max(0, Math.min(1, this.data[i + c])) * 255);
      }
    }
    const header = Buffer.alloc(13);
    header.writeUInt32BE(this.width, 0);
    header.writeUInt32BE(this.height, 4);
    header[8] = 8; // bit depth
    header[9] = 6; // RGBA
    const png = Buffer.concat([
      Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
      chunk("IHDR", header),
      chunk("IDAT", deflateSync(raw)),
      chunk("IEND", Buffer.alloc(0)),
    ]);
    writeFileSync(file, png);
  }
}

export const hex = (s, alpha = 1) => {
  const n = parseInt(s.replace("#", ""), 16);
  return [((n >> 16) & 255) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255, alpha];
};

const smooth = (edge0, edge1, x) => {
  const t = Math.max(0, Math.min(1, (x - edge0) / (edge1 - edge0)));
  return t * t * (3 - 2 * t);
};

/** Soft glowing underline with a diamond marker on the left — a "modern DVD" highlight. */
export function underlineHighlight(w, h, color, { glow = 1 } = {}) {
  const img = new Image(w, h);
  const barY = h * 0.88;
  const marker = h * 0.5;
  // glow bar
  img.paint(color, (x, y) => {
    const fadeX = smooth(0, w * 0.2, x) * smooth(w, w * 0.8, x);
    const dy = Math.abs(y - barY);
    return fadeX * (Math.exp(-(dy * dy) / (2 * (h * 0.05 * glow) ** 2)) * 0.9 + (dy < h * 0.025 ? 0.6 : 0));
  });
  // diamond marker, vertically centred, just inside the left edge
  const cx = marker * 0.55;
  const cy = h * 0.5;
  const r = h * 0.14;
  img.paint(color, (x, y) => 1 - smooth(r - 1, r + 1, Math.abs(x - cx) + Math.abs(y - cy)));
  return img;
}

/** Glowing frame around a thumbnail. */
export function frameHighlight(w, h, color, thickness) {
  const img = new Image(w, h);
  img.paint(color, (x, y) => {
    const d = Math.min(x, y, w - x, h - y); // distance to the outer edge
    const core = d < thickness ? 1 : 0;
    const halo = Math.exp(-((d - thickness) ** 2) / (2 * (thickness * 0.8) ** 2)) * 0.55;
    return Math.max(core, d >= thickness ? halo : 0);
  });
  return img;
}

/** Flat, hard-edged box outline — what a real DVD subpicture highlight looks like. */
export function flatOutline(w, h, color, thickness) {
  const img = new Image(w, h);
  img.paint(color, (x, y) => (Math.min(x, y, w - x, h - y) < thickness ? 1 : 0));
  return img;
}

/** Flat filled box (e.g. a DVD "activated" colour change). */
export function flatFill(w, h, color) {
  return new Image(w, h).paint(color, () => 1);
}
