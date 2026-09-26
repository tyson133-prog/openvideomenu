#!/usr/bin/env node
// Builds the Sintel example: downloads open-licensed media from the Blender Foundation, renders
// DVD-style menu art with ffmpeg, draws highlight overlays, and writes the manifest.
//
//   node examples/sintel/build.mjs          trailer + music & effects track; a stand-in for the film
//   node examples/sintel/build.mjs --film   also downloads the film (1.2 GB) and converts it for browsers
//
// Sintel © Blender Foundation | durian.blender.org — Creative Commons Attribution 3.0.
import { existsSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { download, drawtext, ffmpeg, findFont, writeIgnoreFiles } from "../_tools/ffmpeg.mjs";
import { frameHighlight, hex, underlineHighlight } from "../_tools/png.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const at = (...p) => join(here, ...p);
// Downloads and intermediates live outside the title folder, so copying the folder into a library
// never brings source files along (Plex would scan them as extra versions or movies).
const cache = (...p) => join(here, "..", "_cache", "sintel", ...p);
const wantFilm = process.argv.includes("--film");

const W = 1280;
const H = 720;
const FILM_DURATION = 888; // 14:48
const FEATURE = "Sintel (2010).mp4";
const TRAILER = "Trailers/Sintel Trailer.mp4";
const SCORE = "Other/Music and Effects Track.mp4";
const CHAPTERS = [0, 130, 270, 420, 575, 735]; // seconds; "Chapter n" labels, no scene names claimed

mkdirSync(cache(), { recursive: true });
mkdirSync(at("Trailers"), { recursive: true });
mkdirSync(at("Other"), { recursive: true });
mkdirSync(at("menus"), { recursive: true });
writeIgnoreFiles(at("menus"));
const font = findFont();

// --- media ------------------------------------------------------------------------------------
const trailerSrc = cache("sintel_trailer-480p.mp4");
await download("https://download.blender.org/durian/trailer/sintel_trailer-480p.mp4", trailerSrc);
if (!existsSync(at(TRAILER))) ffmpeg(["-i", trailerSrc, "-c", "copy", "-movflags", "+faststart", at(TRAILER)]);

const scoreSrc = cache("sintel-m+e-st.flac");
await download("https://download.blender.org/durian/movies/sintel-m%2Be-st.flac", scoreSrc);

const filmSrc = cache("Sintel.2010.1080p.mkv");
const standInMarker = cache("feature-is-stand-in");
if (wantFilm) {
  await download("https://download.blender.org/durian/movies/Sintel.2010.1080p.mkv", filmSrc);
  if (!existsSync(at(FEATURE)) || existsSync(standInMarker)) {
    console.log("Converting the film to 720p H.264/AAC MP4 for browsers (takes a few minutes)...");
    ffmpeg(["-i", filmSrc, "-map", "0:v:0", "-map", "0:a:0", "-vf", "scale=1280:-2", "-c:v", "libx264", "-preset", "veryfast",
      "-crf", "21", "-c:a", "aac", "-b:a", "192k", "-ac", "2", "-movflags", "+faststart", at(FEATURE)]);
    rmSync(standInMarker, { force: true });
  }
} else if (!existsSync(at(FEATURE))) {
  console.log("Creating a stand-in for the film (run with --film for the real one)...");
  ffmpeg(["-f", "lavfi", "-i", `testsrc2=size=640x360:rate=10:duration=${FILM_DURATION}`, "-f", "lavfi", "-i",
    `anullsrc=r=48000:cl=stereo`, "-t", String(FILM_DURATION), "-vf",
    [drawtext(font, "Sintel - stand-in for the film", { x: "(w-text_w)/2", y: "h/2-60", size: 30 }),
     drawtext(font, "run  node examples/sintel/build.mjs --film  for the real one", { x: "(w-text_w)/2", y: "h/2-10", size: 18 })].join(","),
    "-c:v", "libx264", "-preset", "ultrafast", "-crf", "35", "-c:a", "aac", "-b:a", "32k", "-shortest", "-movflags", "+faststart", at(FEATURE)]);
  writeFileSync(standInMarker, "");
}

// The trailer is letterboxed inside 854x480; crop to the picture, then fill the frame.
const UNBOX = "crop=854:364:0:58";
const fill = (w, h) => `${UNBOX},scale=${w}:${h}:force_original_aspect_ratio=increase,crop=${w}:${h}`;
const menuFrame = (time, file, filters = "") =>
  ffmpeg(["-ss", String(time), "-i", trailerSrc, "-frames:v", "1", "-vf", `${fill(W, H)}${filters ? "," + filters : ""}`, "-q:v", "3", file]);

// The isolated music & effects track as a video: a still frame over the audio.
if (!existsSync(at(SCORE))) {
  console.log("Building the music & effects extra...");
  const still = cache("score-still.jpg");
  menuFrame(34, still, [
    "eq=brightness=-0.12:saturation=0.8",
    drawtext(font, "Music and Effects Track", { x: "(w-text_w)/2", y: "h*0.78", size: 44 }),
  ].join(","));
  ffmpeg(["-loop", "1", "-framerate", "1", "-i", still, "-i", scoreSrc, "-c:v", "libx264", "-tune", "stillimage", "-preset", "veryfast",
    "-r", "1", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-shortest", "-movflags", "+faststart", at(SCORE)]);
}

// --- menu art ----------------------------------------------------------------------------------
console.log("Rendering menus...");
const gold = hex("#ffcf6a");
const white = hex("#ffffff");

// Main menu: desert dunes, title at the top, three items baked into the art (like a real disc).
const main = {
  x: 780,
  items: [
    { id: "play", text: "Play Movie", y: 430 },
    { id: "scenes", text: "Scene Selection", y: 500 },
    { id: "extras", text: "Special Features", y: 570 },
  ],
};
menuFrame(38, at("menus", "main.jpg"), [
  "eq=brightness=-0.06",
  "vignette=PI/4",
  drawtext(font, "SINTEL", { x: "(w-text_w)/2", y: 70, size: 120, color: "white@0.95", extra: "borderw=1:bordercolor=black@0.3" }),
  ...main.items.map((i) => drawtext(font, i.text, { x: main.x + 40, y: i.y + 12, size: 38 })),
].join(","));
underlineHighlight(420, 64, gold).save(at("menus", "item-selected.png"));
underlineHighlight(420, 64, white, { glow: 1.6 }).save(at("menus", "item-activated.png"));

// v0.2 motion: the dunes drift behind the menu (slowed 2x). The title and items fade in during a
// 2.5 s intro (buttonsAt), then the clip loops from 2.5 s. The still main.jpg stays as the poster.
const MAIN_BUTTONS_AT = 2.5;
const fadeIn = (from, dur) => `alpha='min(max((t-${from})/${dur},0),1)'`;
ffmpeg(["-ss", "36.3", "-t", "3.5", "-i", trailerSrc, "-an", "-vf", [
  "setpts=2.0*PTS",
  fill(W, H),
  "fps=25",
  "eq=brightness=-0.06",
  "vignette=PI/4",
  drawtext(font, "SINTEL", { x: "(w-text_w)/2", y: 70, size: 120, color: "white@0.95", extra: `borderw=1:bordercolor=black@0.3:${fadeIn(0.2, 1.2)}` }),
  ...main.items.map((i) => drawtext(font, i.text, { x: main.x + 40, y: i.y + 12, size: 38, extra: fadeIn(1.3, 1.2) })),
].join(","), "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p", "-movflags", "+faststart", at("menus", "main-loop.mp4")]);

// Menu music: 40 s of the score from the end credits, faded so the loop point is soft.
ffmpeg(["-ss", "800", "-t", "40", "-i", scoreSrc, "-af", "afade=t=in:d=1.5,afade=t=out:st=37:d=3", "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", at("menus", "menu-music.m4a")]);

// Transition into Special Features: 1.4 s of the dragon's flight, with its sound.
ffmpeg(["-ss", "26", "-t", "1.4", "-i", trailerSrc, "-vf", `${fill(W, H)},fps=25,fade=t=in:d=0.25,fade=t=out:st=1.1:d=0.3`,
  "-af", "afade=t=in:d=0.2,afade=t=out:st=1.1:d=0.3", "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p",
  "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", at("menus", "to-extras.mp4")]);

// Scene selection: dark background with a 3x2 grid of chapter thumbnails.
const thumbW = 320;
const thumbH = 180;
const grid = CHAPTERS.map((time, i) => ({
  time,
  n: i + 1,
  x: 120 + (i % 3) * 360,
  y: 170 + Math.floor(i / 3) * 250,
}));
const thumbSource = existsSync(standInMarker) || !existsSync(at(FEATURE)) ? null : at(FEATURE);
for (const g of grid) {
  const file = at("menus", `chapter-${g.n}.jpg`);
  if (thumbSource) ffmpeg(["-ss", String(g.time + 5), "-i", thumbSource, "-frames:v", "1", "-vf", `scale=${thumbW}:${thumbH}:force_original_aspect_ratio=increase,crop=${thumbW}:${thumbH}`, "-q:v", "3", file]);
  // Stand-in: frames from the trailer, spread across it.
  else ffmpeg(["-ss", String(2 + g.n * 5.5), "-i", trailerSrc, "-frames:v", "1", "-vf", fill(thumbW, thumbH), "-q:v", "3", file]);
}
{
  const inputs = grid.flatMap((g) => ["-i", at("menus", `chapter-${g.n}.jpg`)]);
  let chain = `[0:v]${fill(W, H)},eq=brightness=-0.22:saturation=0.6,gblur=sigma=6,${drawtext(font, "Scene Selection", { x: "(w-text_w)/2", y: 60, size: 56 })}[b0]`;
  grid.forEach((g, i) => {
    chain += `;[b${i}][${i + 1}:v]overlay=${g.x}:${g.y}[o${i}]`;
    chain += `;[o${i}]${drawtext(font, `Chapter ${g.n}`, { x: g.x, y: g.y + thumbH + 12, size: 26 })}[b${i + 1}]`;
  });
  chain += `;[b${grid.length}]${drawtext(font, "Main Menu", { x: 1070, y: 652, size: 30 })}[out]`;
  ffmpeg(["-ss", "18", "-i", trailerSrc, ...inputs, "-filter_complex", chain, "-map", "[out]", "-frames:v", "1", "-q:v", "3", at("menus", "scenes.jpg")]);
}
frameHighlight(thumbW + 24, thumbH + 24, gold, 5).save(at("menus", "thumb-selected.png"));
frameHighlight(thumbW + 24, thumbH + 24, white, 8).save(at("menus", "thumb-activated.png"));
underlineHighlight(220, 56, gold).save(at("menus", "small-selected.png"));
underlineHighlight(220, 56, white, { glow: 1.6 }).save(at("menus", "small-activated.png"));

// Special features: the snowy mountain, darkened, with a list.
const extras = {
  x: 150,
  items: [
    { id: "trailer", text: "Trailer", y: 300 },
    { id: "score", text: "Music and Effects Track", y: 370 },
    { id: "all", text: "Play All", y: 440 },
    { id: "back", text: "Main Menu", y: 560 },
  ],
};
menuFrame(4, at("menus", "extras.jpg"), [
  "eq=brightness=-0.2:contrast=1.05",
  drawtext(font, "Special Features", { x: 150, y: 150, size: 64 }),
  ...extras.items.map((i) => drawtext(font, i.text, { x: extras.x + 40, y: i.y + 12, size: 38 })),
].join(","));

// --- manifest ------------------------------------------------------------------------------------
const listButton = (item, x, nav, action, w = 420) => ({
  id: item.id,
  rect: [x, item.y, w, 64],
  label: item.text,
  states: { selected: { image: "menus/item-selected.png" }, activated: { image: "menus/item-activated.png" } },
  nav,
  action,
});
const chapterLabel = (n) => `Chapter ${n}`;

const manifest = {
  format: "menustream",
  version: "0.2",
  title: { name: "Sintel", year: 2010, ids: { imdb: "tt1727587", tmdb: "45745" } },
  source: { kind: "authored", generator: "examples/sintel/build.mjs" },
  media: {
    feature: {
      ref: FEATURE,
      kind: "feature",
      label: "Sintel",
      duration: FILM_DURATION,
      chapters: CHAPTERS.map((time, i) => ({ time, label: chapterLabel(i + 1), thumbnail: `menus/chapter-${i + 1}.jpg` })),
    },
    trailer: { ref: TRAILER, kind: "trailer", label: "Trailer", duration: 52 },
    score: { ref: SCORE, kind: "other", label: "Music and Effects Track" },
  },
  menus: {
    main: {
      label: "Main menu",
      canvas: { width: W, height: H, aspect: "16:9" },
      background: {
        image: "menus/main.jpg",
        color: "#000",
        video: { src: "menus/main-loop.mp4", loopStart: MAIN_BUTTONS_AT },
        audio: { src: "menus/menu-music.m4a" },
      },
      buttonsAt: MAIN_BUTTONS_AT,
      defaultButton: "play",
      buttons: [
        listButton(main.items[0], main.x, { down: "scenes" }, { type: "play", media: "feature" }),
        listButton(main.items[1], main.x, { up: "play", down: "extras" }, { type: "goto", menu: "scenes" }),
        listButton(main.items[2], main.x, { up: "scenes" }, { type: "goto", menu: "extras", transition: "menus/to-extras.mp4" }),
      ],
    },
    scenes: {
      label: "Scene selection",
      canvas: { width: W, height: H, aspect: "16:9" },
      background: { image: "menus/scenes.jpg", color: "#000" },
      autoNav: true,
      buttons: [
        ...grid.map((g) => ({
          id: `ch${g.n}`,
          rect: [g.x - 12, g.y - 12, thumbW + 24, thumbH + 24],
          label: chapterLabel(g.n),
          states: { selected: { image: "menus/thumb-selected.png" }, activated: { image: "menus/thumb-activated.png" } },
          action: { type: "play", media: "feature", chapter: g.n },
        })),
        {
          id: "back",
          rect: [1040, 640, 220, 56],
          label: "Main Menu",
          states: { selected: { image: "menus/small-selected.png" }, activated: { image: "menus/small-activated.png" } },
          nav: { up: "ch6", left: "ch6" },
          action: { type: "back" },
        },
      ],
    },
    extras: {
      label: "Special features",
      canvas: { width: W, height: H, aspect: "16:9" },
      background: { image: "menus/extras.jpg", color: "#000" },
      buttons: [
        listButton(extras.items[0], extras.x, { down: "score" }, { type: "play", media: "trailer" }),
        listButton(extras.items[1], extras.x, { up: "trailer", down: "all" }, { type: "play", media: "score" }, 560),
        listButton(extras.items[2], extras.x, { up: "score", down: "back" },
          { type: "playAll", items: [{ media: "trailer" }, { media: "score" }] }),
        listButton(extras.items[3], extras.x, { up: "all" }, { type: "back" }),
      ],
    },
  },
  rootMenu: "main",
  fallback: [
    { label: "Movie", items: [{ label: "Play Movie", action: { type: "play", media: "feature" } }] },
    {
      label: "Scenes",
      items: CHAPTERS.map((_, i) => ({ label: chapterLabel(i + 1), action: { type: "play", media: "feature", chapter: i + 1 } })),
    },
    {
      label: "Special Features",
      items: [
        { label: "Trailer", action: { type: "play", media: "trailer" } },
        { label: "Music and Effects Track", action: { type: "play", media: "score" } },
      ],
    },
  ],
};
writeFileSync(at("Sintel (2010).menustream.json"), JSON.stringify(manifest, null, 2) + "\n");
console.log(`Done. Preview with:  npm run preview:sintel${existsSync(standInMarker) ? "\n(The film is a stand-in; run with --film for the real one.)" : ""}`);
