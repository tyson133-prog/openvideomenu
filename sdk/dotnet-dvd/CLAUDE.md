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
- **DVD Tamer** (`L:\DVD Tamer Build\DVDTamer`) is one user of the package. It packs it with
  `scripts/pack-openvideomenu.ps1` (which also clears NuGet's cached copy of the version). After a change here,
  re-pack, rebuild DVD Tamer, and check:
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
  - `DiscMapBuilder` simulates first play, then breadth-first resolves every button of every reachable menu (plus menu-table seeds), merges register variants whose buttons behave identically, follows short intro titles through, and builds the title inventory: reach (`OnMenu` / `HiddenButton` / `OffMenu` / `Unreferenced`), targeted chapters, duplicates by cell sector ranges, junk.
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
  - **Size passes (lossless):**
    - `ActionTidy.HoistToMedia` puts on each media entry the keys every play of it shares (when identical) and the prohibitions every play shares.
    - `ActionShare.Compact` merges if-chains whose branches are identical into `any`, and turns repeated subtrees into `shared-N` named actions.
    - Shared actions that start at a pre/post command line don't inline the first PGC they link to.
  - **Clips:** a menu cell without buttons that shows something (video or a still) is a menu without buttons (OVM 0.1.11): its video plays once (`loop: false`), its timeout is the cell's continuation; the highlight register goes through it in `s8`. A program that only loops back to itself (an empty trap cell) is a button-less menu without a timeout (`CompiledMenu.Holds`). Zero-length cells are skipped. Lead-in `transition` chains are no longer built.
  - Art (`MenuArtMaker`, through an `IMenuVideoDecoder`; ffmpeg built in): still/motion backgrounds with music, intro+loop merged with `loopStart`, `buttonsAt`, lead-in cells as `goto.transition`, the disc's highlight PNGs (`MenuStreams`/`Subpicture`/`HighlightRenderer`; not yet for button sets that move); audio/subtitle choices when the caller says the files keep the disc's tracks (`DvdManifestOptions.TrackChoices`; DVD Tamer probes encodes with its `TrackCheck`).
- **Verifier:** `ovm-dvd verify <iso>` builds the whole-disc export (every title "ripped" as virtual media, orphans included) and plays random walks on both the disc VM (`DvdVm`, `StopsAtTitleButtons`) and the SDK `Navigator`: buttons, timeouts, Menu/Title keys, overlay buttons over title-cell menus, parental requests (granted), comparing menu, highlight, GPRMs and playback position. `OVM_DVD_TRACE=walks,vm,compile` traces it (in code: `MenuVerifyOptions.WalkTrace`/`VmTrace`/`CompileTrace`). Diagnostics: `ovm-dvd uops`, `modes`, `sets`, `stats`, `scan`.
- `MenuManifestBuilder`: ids `vmgm-pgc1` / `vts2-pgc4-c3` / `vts1-fr-pgc3`; Back = PGC GoUp, else the nearest on-screen link to a shallower menu; commands kept in `x-dvd`. `DvdExport.ToBundleAsync` validates with the SDK before `Bundle.Pack` and writes through a temp file.
- Verified on the synthetic disc in `OpenVideoMenu.Dvd.Tests` (`SyntheticDisc`: a menu, two titles, a resume button). Add to it when a disc construct needs a test without a real ISO.
