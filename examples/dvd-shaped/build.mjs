#!/usr/bin/env node
// Builds a synthetic "DVD-shaped" title: the menu structure a disc importer would produce.
//   - 720x480 storage canvas shown at 16:9 (anamorphic, non-square pixels)
//   - labels baked into the background; flat hard-edged highlight overlays like DVD subpictures
//   - an episode menu split over two pages with auto-action arrow buttons
//   - a tiny hidden button (easter egg) and x-dvd provenance data
// All media is generated with ffmpeg test sources, so nothing is downloaded.
//
//   node examples/dvd-shaped/build.mjs
import { existsSync, mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { drawtext, ffmpeg, findFont, writeIgnoreFiles } from "../_tools/ffmpeg.mjs";
import { flatFill, flatOutline, hex } from "../_tools/png.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const at = (...p) => join(here, ...p);
const font = findFont();
for (const d of ["Season 01", "Season 00", "menus"]) mkdirSync(at(d), { recursive: true });
writeIgnoreFiles(at("menus"));

// Storage is 720x480 but displayed at 16:9 (853x480). Art is drawn at display size and squeezed,
// exactly as DVD menu video is.
const SW = 720;
const SH = 480;
const DW = 853;
const toStorageX = (x) => Math.round((x * SW) / DW);

// Each episode is a different real-world rip format, so one copy of this show tests them all.
const H264 = ["-c:v", "libx264", "-preset", "ultrafast", "-crf", "30"];
const EPISODES = [
  { id: "s01e01", file: "Season 01/Example Show - s01e01 - Pilot.mp4", title: "Pilot", format: "H.264 + AAC (MP4)",
    codecs: [...H264, "-c:a", "aac", "-b:a", "96k", "-movflags", "+faststart"] },
  { id: "s01e02", file: "Season 01/Example Show - s01e02 - The Second One.mkv", title: "The Second One", format: "H.264 + AC-3 (MKV)",
    codecs: [...H264, "-c:a", "ac3", "-b:a", "192k"] },
  { id: "s01e03", file: "Season 01/Example Show - s01e03 - Finale.mkv", title: "Finale", format: "MPEG-2 + DTS (MKV, like a DVD rip)",
    codecs: ["-c:v", "mpeg2video", "-b:v", "4M", "-c:a", "dca", "-strict", "-2", "-b:a", "768k"] },
];
const EGG = { id: "egg", file: "Season 00/Example Show - s00e01 - Blooper Reel.mp4", title: "Blooper Reel", format: "H.264 + AAC (MP4)",
  codecs: [...H264, "-c:a", "aac", "-b:a", "96k", "-movflags", "+faststart"] };

// --- media: short test clips with chapters -------------------------------------------------------
for (const [i, ep] of [...EPISODES, EGG].entries()) {
  if (existsSync(at(ep.file))) continue;
  ffmpeg(["-f", "lavfi", "-i", `testsrc=size=720x480:rate=24:duration=60`, "-f", "lavfi", "-i", `sine=frequency=${330 + i * 110}:duration=60`,
    "-vf", [
      "setsar=32/27",
      drawtext(font, ep.title, { x: "(w-text_w)/2", y: "h*0.36", size: 44 }),
      drawtext(font, ep.format, { x: "(w-text_w)/2", y: "h*0.36+60", size: 26, color: "yellow" }),
    ].join(","),
    "-pix_fmt", "yuv420p", "-ac", "2", ...ep.codecs, at(ep.file)]);
}

// --- menu art (drawn at 853x480, squeezed to 720x480) ---------------------------------------------
const background = (file, title, lines) =>
  ffmpeg(["-f", "lavfi", "-i", `gradients=size=${DW}x${SH}:c0=0x0b1a3a:c1=0x02050f:x0=0:y0=0:x1=${DW}:y1=${SH}:duration=1:rate=1`,
    "-frames:v", "1", "-vf", [
      drawtext(font, title, { x: 60, y: 40, size: 40, color: "0xe8e0c0" }),
      ...lines.map((l) => drawtext(font, l.text, { x: l.x, y: l.y, size: l.size ?? 28, color: l.color ?? "white" })),
      `scale=${SW}:${SH},setsar=32/27`,
    ].join(","), at(file)]);

// Buttons in display coordinates; converted to storage coordinates for the manifest.
const epButton = (n, y) => ({ x: 90, y, w: 470, h: 50, textX: 110, textY: y + 10 });
const page1 = [epButton(1, 140), epButton(2, 210)];
const page2 = [epButton(3, 140)];
const next = { x: 700, y: 400, w: 60, h: 40 };
const prev = { x: 90, y: 400, w: 60, h: 40 };
const egg = { x: 812, y: 12, w: 14, h: 14 };

background("menus/episodes-1.png", "EXAMPLE SHOW  -  Episodes 1-2", [
  ...page1.map((b, i) => ({ text: `${i + 1}.  ${EPISODES[i].title}`, x: b.textX, y: b.textY })),
  { text: ">>", x: next.x + 12, y: next.y + 4 },
  { text: "*", x: egg.x + 2, y: egg.y - 2, size: 16, color: "0x1a2a4a" }, // barely visible, like a real egg
]);
background("menus/episodes-2.png", "EXAMPLE SHOW  -  Episode 3", [
  ...page2.map((b, i) => ({ text: `${i + 3}.  ${EPISODES[i + 2].title}`, x: b.textX, y: b.textY })),
  { text: "<<", x: prev.x + 12, y: prev.y + 4 },
]);

// Flat highlights at storage size, as a DVD subpicture would be.
const storageRect = (b) => [toStorageX(b.x), b.y, toStorageX(b.x + b.w) - toStorageX(b.x), b.h];
const yellow = hex("#f0d000", 0.9);
const activeFill = hex("#f0d000", 0.35);
const overlays = (name, b) => {
  const [, , w, h] = storageRect(b);
  flatOutline(w, h, yellow, 3).save(at("menus", `${name}-selected.png`));
  flatFill(w, h, activeFill).save(at("menus", `${name}-activated.png`));
  return {
    selected: { image: `menus/${name}-selected.png` },
    activated: { image: `menus/${name}-activated.png` },
  };
};

// --- manifest ----------------------------------------------------------------------------------------
const manifest = {
  format: "menustream",
  version: "0.1",
  title: { name: "Example Show", year: 1999 },
  source: { kind: "dvd", generator: "examples/dvd-shaped/build.mjs (synthetic)" },
  media: {
    ...Object.fromEntries(
      EPISODES.map((ep) => [ep.id, { ref: ep.file, kind: "episode", label: ep.title, duration: 60, chapters: [{ time: 0 }, { time: 20 }, { time: 40 }] }]),
    ),
    egg: { ref: EGG.file, kind: "other", label: EGG.title, duration: 60 },
  },
  menus: {
    "vts1-pgc2": {
      label: "Episodes 1–2",
      canvas: { width: SW, height: SH, aspect: "16:9" },
      background: { image: "menus/episodes-1.png", color: "#02050f" },
      defaultButton: "b1",
      buttons: [
        { id: "b1", rect: storageRect(page1[0]), label: EPISODES[0].title, states: overlays("p1-b1", page1[0]),
          nav: { down: "b2", up: "egg" }, action: { type: "play", media: "s01e01" }, "x-dvd": { button: 1 } },
        { id: "b2", rect: storageRect(page1[1]), label: EPISODES[1].title, states: overlays("p1-b2", page1[1]),
          nav: { up: "b1", down: "next", right: "next" }, action: { type: "play", media: "s01e02" }, "x-dvd": { button: 2 } },
        { id: "next", rect: storageRect(next), label: "Next page", states: overlays("next", next), autoAction: true,
          nav: { up: "b2", left: "b2" }, action: { type: "goto", menu: "vts1-pgc3" }, "x-dvd": { button: 3 } },
        { id: "egg", rect: storageRect(egg), a11yLabel: "Hidden feature", states: overlays("egg", egg),
          nav: { down: "b1" }, action: { type: "play", media: "egg" }, "x-dvd": { button: 4, hidden: true } },
      ],
      "x-dvd": { domain: "VTSM", vtsn: 1, pgcn: 2 },
    },
    "vts1-pgc3": {
      label: "Episode 3",
      canvas: { width: SW, height: SH, aspect: "16:9" },
      background: { image: "menus/episodes-2.png", color: "#02050f" },
      defaultButton: "b3",
      buttons: [
        { id: "b3", rect: storageRect(page2[0]), label: EPISODES[2].title, states: overlays("p2-b3", page2[0]),
          nav: { down: "prev", left: "prev" }, action: { type: "play", media: "s01e03" }, "x-dvd": { button: 1 } },
        { id: "prev", rect: storageRect(prev), label: "Previous page", states: overlays("prev", prev), autoAction: true,
          nav: { up: "b3", right: "b3" }, action: { type: "goto", menu: "vts1-pgc2", button: "b2" }, "x-dvd": { button: 2 } },
      ],
      "x-dvd": { domain: "VTSM", vtsn: 1, pgcn: 3 },
    },
  },
  rootMenu: "vts1-pgc2",
  fallback: [
    {
      label: "Episodes",
      items: [
        { label: "Play all", action: { type: "playAll", items: EPISODES.map((ep) => ({ media: ep.id })) } },
        ...EPISODES.map((ep) => ({ label: ep.title, action: { type: "play", media: ep.id } })),
      ],
    },
    { label: "Hidden features", hidden: true, items: [{ label: EGG.title, action: { type: "play", media: "egg" } }] },
  ],
  "x-dvd": { note: "Synthetic example of what a disc importer writes; not taken from any real disc." },
};
writeFileSync(at("Example Show.menustream.json"), JSON.stringify(manifest, null, 2) + "\n");
console.log("Done. Preview with:  npm run preview:dvd");
