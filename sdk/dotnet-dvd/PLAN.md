# Plan: `OpenVideoMenu.Dvd`, a DVD importer SDK

Status: done 2026-10-03. Written from the DVD Tamer side
(`L:\DVD Tamer Build\DVDTamer`), at the user's request.

## Goal

A DVD SDK that is **as flexible as possible** for developers who turn DVDs into Open Video Menu: rippers,
media-server plugins, library and archiving tools, and test harnesses. Each can use exactly the layer it needs.

**Players never read discs.** They play Open Video Menu bundles, with the core SDK or their own navigator: that is
the point of the format. A DVD is an exporter's input, and this package is for exporters. Players touch it in one
place only: the verifier, which checks a navigator against exports of real discs.

Two principles:
- **Easy on top, open underneath.** One call gives a full, faithful export with sensible defaults. Every layer
  below it is public and usable on its own: reading discs, decoding commands, running the DVD machine,
  translating, making art, verifying.
- **Defaults are faithful; departures are explicit.** Out of the box every export is the whole disc, with art.
  Anything less (some titles only, no art, a different media layout) is an option the developer asks for. Nothing
  is ever dropped silently.

DVD Tamer becomes one user of the package. It uses the defaults: full, faithful menus.

## Why

**The point is adoption** (the user's reason): a format spreads when developers have tools for it. A complete,
faithful DVD importer that anyone can build on makes producing Open Video Menu easy, and so makes the format
worth supporting.

DVD Tamer already contains a complete DVD → Open Video Menu importer:
- a disc reader;
- a model of the DVD virtual machine;
- a translator from disc programs to Level 3 manifests;
- a verifier that plays both sides against each other.

On 2026-10-02 it exported all 954 ISOs of the user's collection with no differences from the disc model. The
exports total 119 MB (median 65 KB, largest 4.6 MB).

Today that code only exists inside a ripping app. Packaging it next to the spec would:
- **make the DVD profile checkable where it's written.** Gaps 1–27 were found by DVD Tamer's verifier and passed
  on through notes. With the package here, a change to `spec/profiles/dvd.md` or the navigator can be run against
  real discs by whoever makes it.
- **let other tools produce Open Video Menu from DVDs** (media-server plugins, other rippers) without DVD Tamer.
- **keep the core SDK format-neutral.** Players and authoring tools keep depending on `OpenVideoMenu` alone.
  Disc reading and the DVD machine live in `OpenVideoMenu.Dvd`, which depends on it.

## Decisions

Decided 2026-10-03: 1 (yes, a separate package), 2 (Apache 2.0), 3 (the proposed names), 4 (replace ImageSharp), 5 (offer
partial exports as an option), 6 (the collection check stays local).

1. **The earlier rule.** "DVD-specific tooling doesn't belong in menustream" (DVD Tamer's `CLAUDE.local.md`,
   2026-09-30). This plan reverses it for a separate package. The spec itself stays DVD-neutral, with the profile
   as the only DVD text.
2. **License.** menustream is Apache 2.0. DVD Tamer has no license file. The moved code would be released under
   Apache 2.0. It's the user's own code, written from the public format docs, with nothing from libdvdnav (GPL).
3. **Name.**
   - Proposed package name: `OpenVideoMenu.Dvd`. It's a part of this project, so it fits NAMING.md.
   - Proposed CLI tool: `ovm-dvd`.
4. **ImageSharp.** The highlight images use SixLabors.ImageSharp, whose split license asks larger commercial users
   for a paid license.
   - Either replace it with a small built-in PNG writer (the images are 4-colour palette bitmaps), or keep it.
   - Proposed: replace it, so the package carries only MIT/Apache dependencies (DiscUtils is MIT).
5. **Partial exports.** The package would offer chosen-titles exports as an explicit option, as the DVD profile
   allows (SHOULD, not MUST). DVD Tamer keeps its policy: the whole disc.
6. **Where the corpus lives.** Discs can't be committed. The full-collection check stays local, like today:
   `DVDTAMER_TEST_ISO_DIR`, `\\mediacenter`, `verify-all.ps1`. Committed tests use synthetic or tiny inputs only.

## What moves (about 9,600 lines)

| From DVD Tamer (`DVDTamer.Core/…`) | Lines | Into the package |
|---|---:|---|
| `DvdFilesystem/*` (ISO via DiscUtils UDF/ISO 9660, VIDEO_TS folders) | 280 | `IO/` |
| `DvdStructure/IfoReader.cs`, `IfoModels.cs`, `NavPackReader.cs` | 870 | `Structure/` |
| `DvdStructure/DvdDisc.cs` | 490 | `Structure/` |
| `DvdStructure/DvdCommands.cs`, `DvdInstruction.cs` | 720 | `Machine/` |
| `DvdStructure/DvdVm.cs` | 910 | `Machine/` |
| `DvdStructure/DiscMap.cs` (`DiscMapBuilder`: reachability, title inventory, duplicates) | 540 | `Machine/` |
| `Menus/DvdMenuCompiler.cs` | 3,240 | `Export/` |
| `Menus/MenuManifestBuilder.cs` (after the split below) | ~600 | `Export/` |
| `Menus/ActionTidy.cs`, `ActionShare.cs`, `ActionTree.cs` | 600 | `Export/` |
| `Menus/PlaybackMapper.cs` (`CellMediaIndex`, `IPlaybackMapper`) | 310 | `Export/` |
| `Menus/MenuArt.cs` (ffmpeg, path given), `MenuSubpicture.cs` (highlights) | 470 | `Art/` |
| `Menus/MenuVerifier.cs` | 460 | `Verify/` |
| CLI commands `map`, `nav`, `verify`, `stats`, `scan`, `uops`/`modes`/`sets` | — | `ovm-dvd` tool |
| Tests: `DvdInstructionTests`, the DVD parts of `MenuExportTests`/`DiscIntegrationTests` | — | `OpenVideoMenu.Dvd.Tests` |

## What stays in DVD Tamer

- Ripping and encoding: MakeMKV, HandBrake, ffmpeg MP4 (`Core/Ripping`).
- Naming and metadata: `Core/Naming`.
- OCR and menu labels: `Core/Ocr`.
- The live menu tracker and scout: `Core/Sync`, `DVDTamer.Playback`. They use the package's VM and disc model.
- The UI, settings, queue and destination folders.
- Two pieces of the export that are DVD Tamer policy:
  - `MediaCatalog`: ripped jobs → media entries, and the fallback list from episodes and extras.
  - `MenuExporter`: bundle folder and name, whole-disc queuing, `TrackCheck`, `ExistingRip`, `OrphanClips`
    (the "disc clips" folder).
  - It calls the package with art on: DVD Tamer keeps exporting full, faithful menus.

## The API, in layers

Each layer depends only on the ones above it in this list, and each is public.

### 1. Disc access (`OpenVideoMenu.Dvd.IO`)
- **`IDvdFileSystem`**: open a file by name, read sectors.
  - Built in: ISO images (UDF, with ISO 9660 as fallback; DiscUtils), VIDEO_TS folders.
  - Custom: a developer can supply a physical drive, a network stream, an archive, or an in-memory image for
    tests.
- `DvdDisc.Open(path)` picks the built-in one. `DvdDisc.Open(IDvdFileSystem)` takes any.
- No CSS decryption: inputs are decrypted, as in DVD Tamer.

### 2. Structure (`.Structure`)
- **The IFO model**, read-only records:
  - title search table, title sets, chapter (PTT) tables;
  - PGCs with programs, cells, commands, UOPs and playback modes;
  - menu language units;
  - video, audio and subpicture attributes;
  - palettes.
- **Navigation packs:** highlight info per cell, every button set and when it shows, buttons (rects, neighbours,
  commands, groups), and VOBU UOPs.
- **Sectors of a cell** (`ReadMenuCell`, `ReadTitleCell`), for callers that decode video themselves.

### 3. Commands (`.Commands`)
- A decoder from 8-byte commands to a typed instruction tree (`DvdInstruction`: conditions, sets, links, jumps).
- A disassembler to text (what `map --verbose` prints).
- An evaluator over a register state.
- Usable for analysis tools, linters, or another VM.

### 4. The DVD machine (`.Machine`)
- **`DvdVm`**: first play, pre/cell/post commands, buttons, Menu and Title keys (MenuCall/TitleCall), resume,
  still times, random and shuffle PGCs, parental requests, the navigation timer.
  - `VmState` is clonable: registers, domain, position and the resume point.
  - It stops wherever a DVD player would wait.
- **Hooks:** an optional observer for every PGC entry, cell, command and link, which is today's
  `DVDTAMER_VM_TRACE`, as an API. Also settings for what differs between DVD players: initial SPRMs, region,
  parental level, preferred languages, and the aspect.
- **`DiscMapBuilder`**: reachability of every menu, every button's target, the title inventory (on menu, hidden,
  unreferenced), duplicates and junk.
- **What it's for:**
  - the translator and the verifier use it;
  - so does DVD Tamer's live menu tracker, which follows the real menu playing in libVLC while the user picks
    titles;
  - exporters can use it to analyse a disc (what's reachable, hidden titles) before ripping.

### 5. Translation (`.Export`)
- **`DvdTranslator`:** disc → `Manifest`, with the pieces the caller wants to control as inputs.
  - **Media layout:** `CellMediaIndex` / `IPlaybackMapper` says where each disc cell is: which file, at what
    time. Helpers cover the common layouts: one file per title (MakeMKV/HandBrake), per PGC, angles as separate
    files, chapters split into episodes. A custom mapper covers anything else.
  - **Scope:** the whole disc by default. Or chosen titles only, where buttons to titles left out do nothing and
    the result says how many.
  - **Tracks:** whether the files keep the disc's audio and subtitle order, globally or per media. That decides
    whether the menus' language choices are written.
  - **Labels and identity:** `IMenuLabels` gives button captions and page headings (DVD Tamer: OCR). Also the
    title and year, the fallback list, and the ids of menus and media (defaults like `vmgm-pgc1`, `t3`).
  - **Menu language:** which language unit is chosen at first play.
- **Passes** (merge identical actions, move keys and prohibitions to media, share repeated logic, drop unread
  sets) are on by default. They can be turned off to see the raw translation, which is useful when debugging a
  disc. A compile trace callback is available too.
- **Result:** the manifest, plus warnings with codes: unripped titles, spec gaps found, track mismatches.
  - It includes the menu cells that need art, so any art source can fill them.

### 6. Art (`.Art`)
- **`IMenuArtSource`:** gives a menu cell's still frame, motion clip, music and loop points.
  - Built in: ffmpeg (path, or found on PATH).
  - Custom: an exporter's own MPEG-2 decoder, a cloud transcoder, or none (plain backgrounds, with a warning).
- **The highlight renderer:** decodes subpictures and applies the button colour tables and palette, to RGBA or to
  PNG with a built-in writer.
- Each art kind can be switched on or off: stills, highlights, motion, music. All are on by default.

### 7. Bundles (`.Export`)
- **`DvdExport.ToBundleAsync(disc, files, path, options)`:** translation + art + validation + `Bundle.Pack`, in
  one call. The default is full and faithful. This is what DVD Tamer calls.
- **`DvdExport.ToManifest(disc, files, options)`:** the manifest in memory, for exporters that write the bundle
  their own way.
- Cancellation and progress reporting throughout. No global state, and safe to run on several discs at once.

### 8. Verification (`.Verify`)
- **`DvdVerifier.Run(disc, options)`:** plays random walks of the disc VM against the core SDK's `Navigator` over
  the export, and reports the differences with the steps that lead to them.
  - Options: walks, depth, seed, the events used (buttons, keys, timeouts, overlays, parental), the menu language.
- **Pluggable both ways:**
  - check any exporter's manifest, not only this package's;
  - check any navigator (another SDK or player), through a small adapter.
- **What it's for:**
  - exporters test themselves;
  - the spec checks its DVD profile;
  - player developers check that their Open Video Menu navigator plays exported discs as the discs would.

### 9. Tool (`ovm-dvd`)
- The layers as commands: `map`, `nav`, `disasm`, `export`, `verify`, `verify-all <folder>`, `stats`, `scan`,
  `uops`.
- `--json` output everywhere, for scripts.

### Packaging
- The library targets net8.0 and net10.0, like the core SDK.
- One NuGet package (`OpenVideoMenu.Dvd`), versioned with the DVD profile. Plus the `ovm-dvd` dotnet tool.
- Public types are documented. Internals stay internal: the compiler's path machinery and the join analysis.

## Progress

| Step | State |
|---|---|
| 1. Untangle inside DVD Tamer | Done 2026-10-03: `DvdManifestOptions` replaces `DiscPlanner`/`MediaCatalog`/`RipJob`/`MenuExportLevel` in the builder; DVD Tamer fills it in `MediaCatalog.Builder`. |
| 2. A project inside DVD Tamer | Done 2026-10-03: 23 files moved (git history kept there); ImageSharp replaced by `RgbaImage` (own PNG writer; every highlight decodes to the same pixels). |
| 3. Move it here | Done 2026-10-03: `sdk/dotnet-dvd/OpenVideoMenu.Dvd` (net8.0 + net10.0, Apache-2.0, 0.1.0); DVD Tamer uses the packed package. |
| 4. Open the layers | Done 2026-10-03. Public without `InternalsVisibleTo`; `DvdPlayerSettings` and `DvdVm.Observer` (no environment variables in the package); `IMenuVideoDecoder` + `FfmpegMenuDecoder` and `MenuArtKinds`; `DvdExport.ToBundleAsync` (art, build, validate, pack; DVD Tamer's exporter calls it); `DvdMedia.OneFilePerTitle`; `ManifestPasses`; coded warnings (`DvdWarning`); verifier `MenuVerifyOptions` with `Export` (another exporter's manifest) and `Navigator` (another navigator) adapters; XML docs. |
| 5. Tests and checks | Done 2026-10-03. `OpenVideoMenu.Dvd.Tests`: 23 tests moved from DVD Tamer, plus `SyntheticDisc` (a disc built in memory) with 12 end-to-end tests (read, map, run, observer, export, partial export, verify own and external manifests, navigator adapter, bundle with and without art). The synthetic disc found a translator bug (a resume reached from a button lost its highlight), now fixed. |
| 6. The tool | Done 2026-10-03. `ovm-dvd`: map, nav, disasm, export, verify, verify-all (one process per disc, timeout, logs, grouped summary, sizes), stats, scan, uops, modes, sets. DVD Tamer's CLI dropped the commands that moved. |
| 7. Docs | Done 2026-10-03. `README.md` and `CLAUDE.md` here (the working agreement and how it works); a pointer in `spec/profiles/dvd.md`; DVD Tamer's CLAUDE.md now covers its policy and the boundary. |
| 8. Working agreement | Written in `CLAUDE.md` here. |
| 9. The full check | Done 2026-10-03. `ovm-dvd verify-all` over DVD1–3: 954/954 OK, no timeouts, no invalid exports; sizes unchanged (median 65 KB, 90th percentile 252 KB, largest 4.6 MB, total 119 MB). A real rip with menus on (Green Nose title 6, MKV and built-in MP4): the bundles validate, and the loose manifest plays in the web player. Opened as `.ovmz`, the web player resolves media inside the bundle (against spec §2.1): a player bug, reported. |

Each step's check: the five discs below verify with 0 differences, and their verify exports are byte-identical to the
baseline taken before step 1. DVD Tamer's own export path (`menus`, with planner, catalog and art) on two discs
(Green Nose, Folks!) gives byte-identical manifests, and highlight images decode to identical pixels.

Findings from step 4:
- **Tracks per media** can't be an exporter option. A disc's SetSTN becomes `@audio`/`@subtitle`, which
  applies to whatever plays next. Files that keep the disc's tracks and files that don't would need a track map
  per media, which the format doesn't have. That's a spec question if it's ever wanted. For now
  `TrackChoices` covers the whole export.
- **Partial exports** need no code. Titles the caller gives no file for are already buttons that do nothing,
  counted in `UnrippedButtons` and `MissingTitles`. "Choose titles" means "give files for those titles".

## Steps

Each step ends with DVD Tamer's tests passing and five of the hardest discs verifying with 0 differences, their
exports unchanged byte for byte. Each disc covers different machinery:

| Disc | What it exercises |
|---|---|
| The Matrix Revolutions, Disc 2 | Title-cell menus (overlays, 16 buttons each), post-command dispatchers, two title sets with the same content; the second-largest export |
| The Lord of the Rings: The Return of the King, Disc 3 | The largest export: 130 menus, 898 buttons, shared logic everywhere |
| The Lion King, Disc 2 | A game disc: about 1,750 buttons in 568 title cells, overlay seeks, resume points |
| If I Only Had A Green Nose | Overlay focus (`@focus`), forced selects, Menu/Title keys during title-cell menus |
| Baby Genius Baby Animals Favorite Sing-A-Longs | Resumes to an earlier cell, the live chapter register, the highlight carried into titles |

The full collection (954 ISOs, about 40 minutes) is verified once, at the end (step 9).

1. **Untangle inside DVD Tamer.**
   - Put the interfaces above between `MenuManifestBuilder` and `DiscPlanner`/`MediaCatalog`/`RipJob`.
   - Move the fallback list and labels behind `IMenuLabels`/`Fallback`.
   - Replace `MenuExportLevel` in the compiler and verifier with options.
   - No files move yet.
2. **A project inside DVD Tamer's solution.**
   - Create `OpenVideoMenu.Dvd` there and move the files listed above, with namespaces `OpenVideoMenu.Dvd.*`.
   - `DVDTamer.Core` references it.
   - Replace ImageSharp if decided.
3. **Move it here.**
   - The project goes to `sdk/dotnet-dvd/OpenVideoMenu.Dvd` (+ `.Tests`, + `ovm-dvd`), with a solution next to
     `sdk/dotnet`.
   - Pack it into DVD Tamer's `local-packages/` the way `scripts/pack-openvideomenu.ps1` packs the core.
   - DVD Tamer switches its project reference to the package.
4. **Open the layers.** This is new work; today these are internal or environment variables.
   - `IDvdFileSystem` as a public extension point.
   - VM observer hooks and DVD-player settings (region, parental level, languages), replacing `DVDTAMER_VM_TRACE` and the fixed SPRM defaults.
   - `IMenuArtSource`, with ffmpeg as one implementation; the art kinds switchable one by one.
   - Translator options: scope, per-media tracks, ids, turning passes off, a trace callback (replacing
     `DVDTAMER_COMPILE_TRACE`).
   - Warning codes.
   - Verifier adapters for other exporters and other navigators.
   - Public API docs.
   - DVD Tamer's own use must not change behaviour: the five discs' exports stay the same byte for byte.
5. **Tests and checks.**
   - Unit tests for the command decoder, VM and translator, on synthetic PGCs. The existing `DvdInstructionTests`
     style builds commands from bytes.
   - A small synthetic VIDEO_TS built in tests, for reader and export round trips.
   - `ovm-dvd verify-all <folder>` replaces DVD Tamer's scratch `verify-all.ps1`, with a summary like `summ6.py`.
     That makes the collection check a documented command.
6. **The CLI tool `ovm-dvd`:**
   - `map <iso>`: menus, buttons and targets, title inventory; `--verbose` adds commands.
   - `verify <iso> [--walks] [--json out]`, and `verify-all <folder>`.
   - `export <iso> --media <dir>`: a bundle from files ripped one per title.
   - `stats`, `scan`, `uops`, `nav`: the diagnostics.
7. **Docs.**
   - `sdk/dotnet-dvd/README.md`.
   - A pointer from `spec/profiles/dvd.md`: "a reference importer and verifier are in `sdk/dotnet-dvd`".
   - CLAUDE.md in both repos. The menustream side says profile changes are checked with `ovm-dvd verify-all`.
   - DVD Tamer's "Disc menus" section shrinks to its policy and the boundary.
8. **Working agreement.**
   - The spec agent can then run the verifier on profile and navigator changes itself.
   - Spec gaps still go to the user as notes. The verifier only finds them.
   - DVD Tamer changes to the translator are made here and re-packed.
9. **The full check.**
   - `ovm-dvd verify-all` over the whole collection: all 954 ISOs must verify, with no timeouts and no invalid
     exports.
   - Compare the export sizes with the 2026-10-02 run: median 65 KB, total 119 MB. They must match unless a change
     was meant to alter them.
   - Run DVD Tamer's app once with menus on: one real rip, its bundle checked with the web player's preview.

## Risks

- **Two repos change together** while step 3 settles. Mitigation: steps 1–2 happen in DVD Tamer first, so the
  move in step 3 is mechanical.
- **The disc model is reference, not truth.** It follows the public format docs and was checked against real
  discs through libVLC by DVD Tamer's tracker. It's still one implementation.
  - Its known choices are recorded: SPRM 7 live, SPRM 8 on a forced select, random draws as the spec's generator.
  - Differences from hardware players are possible. Say so in the README.
- **DVD Tamer's live tracker and scout depend on `DvdVm` and `DvdDisc` internals** (`StopsAtTitleButtons`,
  `ContinueAfterStill`, highlight caches). These must stay public in the package, or be wrapped.
- **Performance.** A whole-collection verify takes about 40 minutes at 3 in parallel. Exporting reads every title
  cell's navigation packs for overlays. Fine for an SDK, but document it.
