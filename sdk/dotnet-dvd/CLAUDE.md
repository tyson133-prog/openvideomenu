# Working in sdk/dotnet-dvd

`OpenVideoMenu.Dvd` is the DVD importer. It turns discs into Open Video Menu manifests and checks them against the
disc. The plan and its progress are in `PLAN.md`; what the package offers is in `README.md`.

- **The DVD profile and the importer move together.** When `spec/profiles/dvd.md` or a navigator changes, run the
  check and look at every difference before calling the change done:
  - `dotnet test OpenVideoMenu.Dvd.Tests`;
  - `ovm-dvd verify-all <folder of discs> --out <dir>` (the user's collection is on `\\mediacenter\DVD1-3`,
    read-only).
- **Spec gaps go to the user.** When the format can't express something a disc does faithfully, or only at absurd
  size, stop and tell the user, and don't work around it. The verifier only finds gaps; the user decides on the
  spec.
- **Faithful by default.** Exports are the whole disc's logic. Anything less (fewer titles, no art) is an option
  the caller asks for, never something dropped silently.
- **DVD Tamer** (`L:\DVD Tamer Build\DVDTamer`) is one user of the package. This repo packs it:
  `scripts/pack-nuget.ps1` writes the packages to `L:\nuget-local` (DVD Tamer's local package source until they're
  on nuget.org) and clears NuGet's cached copy of each version. After a change here, re-pack, rebuild DVD Tamer
  (a new version number is DVD Tamer's change to make: leave it a note), and check:
  - five hard discs byte for byte (Matrix Revolutions D2, Return of the King D3, Lion King D2, Green Nose,
    Baby Genius Baby Animals);
  - the whole collection at the end of the work.
- The package reads no environment variables: tracing is through callbacks. `ovm-dvd` reads `OVM_DVD_TRACE`.
- Players never read discs. Nothing here is for players except the verifier's navigator adapter.

## How it works

- **The disc map** (`.Structure`, `.Commands`, `.Machine`).
  - `IfoReader` parses VIDEO_TS.IFO / VTS_nn_0.IFO (falls back to .BUP): title search table, per-VTS chapter (PTT) tables, title PGCs, menu PGCI_UT language units, PGC command tables, cells, video/audio/subpicture attributes.
  - `NavPackReader` reads **menu buttons from PCI packets in NAV packs of the menu VOBs** (VIDEO_TS.VOB, VTS_nn_0.VOB) — rectangles, up/down/left/right neighbors, 8-byte commands, button groups (the wide group is chosen for 16:9 menus). Buttons are *not* in the IFO; that misunderstanding (plus wrong header offsets) is why an earlier IFO attempt (in DVD Tamer's history) failed. NAV packs and IFOs are never CSS-scrambled.
  - `DvdCommands` evaluates every VM command group; `DvdVm` runs PGC pre/cell/post commands with clonable `VmState` (GPRM/SPRM) and stops where a player would wait (menu with buttons) or start a title. Written from the public format docs, not ported from libdvdnav (GPL).
  - `DiscMapBuilder` simulates first play, then breadth-first resolves every button of every reachable menu (plus menu-table seeds), merges register variants whose buttons behave identically, follows short titles through (under `FollowThroughBelow`, 60 s), and builds the title inventory: reach (`OnMenu` / `HiddenButton` / `OffMenu` / `Unreferenced`), targeted chapters, duplicates by cell sector ranges, junk. A **duplicate** (`DuplicateOf`) plays the same video, so the original's file plays it: exactly the same cells, or (a **near duplicate**, `IsNearDuplicate`) at least 90% of them with the same length plus a few cells of its own, listed in `OwnCells`. The Matrix's White Rabbit mode (title 6) is one: 18 own cells, 244 s. A ripper that rips only the original must rip those too. **A button's title** (`ButtonTarget.Landing`) is the last title in its chain with real content, i.e. not junk (under `DvdTitleInfo.JunkBelow`, 3 s; an orphan PGC by the same length): a real pre-roll before the film stays in `PassedThrough`, and an empty stub after an extra (a title that only returns to the menu) goes to `FollowedBy`, so the extra is `OnMenu` and the stub stays junk. A chain of nothing but junk keeps its last title, unless it goes on to another menu: a stub that hands back to the menus, which play on (through button-less cells and commands, as the VM does) to a menu that waits for the viewer, makes the button lead to that menu (`TargetKind.Menu`, the stubs in `PassedThrough`). Back to the same menu (or one with the same layout), it stays a title. M\*A\*S\*H Seasons 5–6: each episode button plays a 0.5 s stub, then opens that episode's own menu (Play, scenes, …). Junk is decided before buttons are credited (`TitleLength` measures a title the same way while menus are mapped).
- **Translation, not simulation.** `DvdMenuCompiler` translates the disc's programs (first play, PGC pre/post/cell commands, button commands) into OVM actions: GPRMs → `vars`, SPRMs → `@` player state or shadow vars, conditions → `if` (forks rejoin via ConditionalSet/TrySkip/TryJoin), title playback → `play` of the ripped files matched by cell sector ranges (`CellMediaIndex`; runs over several files become `segments` composites). Every export is `"level": 3`, `resumePoint: "untilUsed"`, `cuePlayback: "replace"`.
  - **Named actions** (`actions`/`run`) keep it small: every PGC entry (menu or title domain), links to a program/cell of a title PGC outside playback, overlay buttons, keys and resume continuations are shared actions compiled once from whole-disc register values (`_globalG`); identical ones are merged afterwards (`ActionTidy.MergeIdenticalActions`). Title playback never runs inline into another title PGC (chained PGCs are consecutive plays); a title's post commands are their own shared action (`-post`), so they test registers live, also after a resume. A backward `Goto` in pre/post commands (a disc's loop, e.g. drawing random numbers until one is unused) runs a shared action starting at that line (`-pre-lN`), which runs itself each time round. A condition in a cell command during playback ends the play there (it's decided where the disc reaches it, not when playback starts).
  - **Resume:** CallSS out of a title → `saveResume` ("here" in cues; with `item` = the resumed title's whole play item and `after` = its post-command continuation when compiled in shared logic); JumpSS/JumpTT → `clearResume`; LinkRSM → `resume`.
  - **Title position** (SPRM 4–7): read live from `@lastMedia`/`@lastChapter` through the shared `player-position` action, except after a link outside playback set them (`_pos` flag). Media get aliases (`-ttN`) when two titles/PGCs play the same file.
  - **Playback details:** cell commands → point cues; LinkCN/PGN/PTTN within the playing title and self-loops → `seek`; still cells → `holds` and buttons in title cells → `overlays`, both on the **media** (they belong to the cells); random/shuffle PGCs → `playAll.order/count`; SetNVTMR → `timer`; counters; SetTmpPML → `requestParental`; Exit → `exit`; other button groups → `altRects`; SPRM 11 → `@karaoke`; menu languages → units chosen by `@menuLanguage`; remote keys (Menu/Title/audio/subtitle/angle/chapter, a do-nothing action where the disc ignores the key); `prohibits` = PGC UOPs (+title), `prohibitsAt` = the cells' navigation-pack UOPs on the media; menus whose buttons change mid-video → `buttonSets`.
  - **Registers that are easy to get wrong:**
    - SPRM 7 is the live chapter in both the VM and the export. After a resume it holds the saved chapter until the next one starts (spec §6.5).
    - SPRM 8 is kept in `s8`, set from several places:
      - from the menu's `@focus` when a menu leaves for a title or a resume (the shared `focus-to-s8` action);
      - from LinkRSM's button field, else the button pressed when a resume is reached from a button (the synthetic disc's resume button: its title's post commands call the menu, which opens on it);
      - during titles, from the overlay's `@focus` while one shows (`overlay-focus-to-s8`: `@focus` is `""` without one, OVM 0.1.14), in title keys and cell commands. Not in pre/post commands: after playback `@focus` is the menu's again.
    - Overlays without a forced select open on `{"var":"_hl"}`, the id of s8's button: `ActionTidy.TrackHighlightId` adds a `_hl` set after every s8 set.
    - A title cell command's SetHL_BTNN also sets `@focus`.
    - In the VM, a title cell's forced select sets SPRM 8 as its buttons appear.
    - Random draws are never dropped as unread: each one moves the generator on.
    - The whole-disc register analysis includes the buttons in title cells.
    - An overlay's link to a cell past its play item's end is a new play, not a `seek`.
  - **Chapters are places in the cell list, not times in the video.** A title that lists the same video again
    (one 3 s clip 24 times, or several chapters on one 29 s clip as in Matrix Revolutions D2's title 15) plays as a
    composite that follows the disc's list, so its timeline has the clip each time it's listed, while a rip that holds
    each distinct cell once (`onceEach`, `ovm-dvd export --cells-once`) holds it once. A composite's chapters are
    where the disc's chapters start (`PlayedCell.ChapterStart`: the entry cell of a program a chapter table lists),
    not one per segment. A file's own chapter list keeps only marks that move forward; marks at or before the one
    before them (a ripper's chapters on video it holds once) are dropped with `ChaptersNotForward`. Plays use
    times and the position map (`PlayerPosition`), so nothing they play moves.
  - **Cells no file holds:** inside a run that otherwise plays from the rip, they're skipped (the film still plays) with a `CellsNotRipped` warning naming them, once each (`MenuManifestBuilder.CellsNotRipped`). A whole run with no file stays as before: under `SkipUnrippedBelow` (a logo, a warning) it's passed over silently, longer it's an unavailable play.
  - **Size passes (lossless):**
    - `ActionTidy.HoistToMedia` puts on each media entry the keys every play of it shares (when identical) and the prohibitions every play shares.
    - `ActionShare.Compact` merges if-chains whose branches are identical into `any`, and turns repeated subtrees into `shared-N` named actions.
    - Shared actions that start at a pre/post command line don't inline the first PGC they link to.
  - **Clips:** a menu cell without buttons that shows something (video or a still) is a menu without buttons (OVM 0.1.11): its video plays once (`loop: false`), its timeout is the cell's continuation; the highlight register goes through it in `s8`. A program that only loops back to itself (an empty trap cell) is a button-less menu without a timeout (`CompiledMenu.Holds`). Zero-length cells are skipped. Lead-in `transition` chains are no longer built.
  - **Motion or still, one rule for every cell:** a cell whose video has 3 or more pictures is motion
    (`MenuStreams.CountPictures`), whatever its length; fewer is a still picture (with its sound as
    `background.audio` when it has any). A cell that holds when its video ends (any still time, timed or
    infinite) plays its video and music once (`loop: false`) and keeps the last frame, which is also its poster;
    with no hold it loops. An intro merged with its loop cell follows the loop cell's hold.
  - Art (`MenuArtMaker`, through an `IMenuVideoDecoder`; ffmpeg built in): still/motion backgrounds with music, intro+loop merged with `loopStart`, `buttonsAt`, lead-in cells as `goto.transition`, the disc's highlight PNGs (`MenuStreams`/`Subpicture`/`HighlightRenderer`) for menus, their button sets (`{id}-s{n}-b{b}-*.png`) and overlays (`vts{v}-nav{sector}-b{b}-*.png`, drawn while building through `MenuManifestBuilder.OverlayArt`), each from the subpicture complete within its highlight's video unit (`MenuStreams.SubpictureAt`); audio/subtitle choices when the caller says the files keep the disc's tracks (`DvdManifestOptions.TrackChoices`; DVD Tamer probes encodes with its `TrackCheck`).
- **Verifier:** `ovm-dvd verify <iso>` builds the whole-disc export (every title "ripped" as virtual media, orphans included) and plays random walks on both the disc VM (`DvdVm`, `StopsAtTitleButtons`) and the SDK `Navigator`: buttons, timeouts, Menu/Title keys, overlay buttons over title-cell menus, parental requests (granted), comparing menu, highlight, GPRMs and playback position. `OVM_DVD_TRACE=walks,vm,compile` traces it (in code: `MenuVerifyOptions.WalkTrace`/`VmTrace`/`CompileTrace`). Diagnostics: `ovm-dvd uops`, `modes`, `sets`, `stats`, `scan`.
- `MenuManifestBuilder`: ids `vmgm-pgc1` / `vts2-pgc4-c3` / `vts1-fr-pgc3`; Back = PGC GoUp, else the nearest on-screen link to a shallower menu; commands kept in `x-dvd`. `DvdExport.ToBundleAsync` validates with the SDK before `Bundle.Pack` and writes through a temp file.
- Verified on the synthetic disc in `OpenVideoMenu.Dvd.Tests` (`SyntheticDisc`: a menu, two titles, a resume button). Add to it when a disc construct needs a test without a real ISO.
