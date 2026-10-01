# DVD import profile (early draft)

This profile describes how a tool that reads a DVD's menu structure (from an unencrypted disc image or VIDEO_TS folder) should express it as a manifest, so any importer produces files every player understands. It covers Level 1 today. Level 2 items (motion menus, menu timeouts, variables, audio/subtitle setup, angles, segments and cues during playback) have their own rows; what the core can't express yet is listed at the end, for importers to keep in `x-dvd`.

## What to export

An importer that exports a disc's menus SHOULD export the whole disc: every title its navigation can
reach, all audio and subtitle tracks, and the complete logic, so the menus behave as the disc does.
Partial exports (only some titles, simplified logic) are valid manifests; how faithful to be is the
importer's choice.

## Mapping

| Disc concept | Manifest |
|---|---|
| FirstPlay PGC | `firstPlay` (e.g. `play` of a warning/logo clip). If FirstPlay just jumps to a menu, set `rootMenu` and omit `firstPlay`. |
| VMGM / VTSM menu PGC | One entry in `menus`. Use a stable id such as `vts1-pgc2` or `vmgm-pgc1`. |
| Menu video frame size | `canvas.width/height` = storage size (720×480 NTSC, 720×576 PAL). |
| Menu display aspect | `canvas.aspect` = `"16:9"` or `"4:3"` from the video attributes. |
| Still menu background | `background.image`: the menu's still frame without the highlight subpicture, as PNG. |
| Button rectangle (PCI: X0,Y0,X1,Y1 inclusive) | `rect: [X0, Y0, X1 - X0 + 1, Y1 - Y0 + 1]` |
| Button neighbours (up/down/left/right) | `nav`, using the manifest ids of those button numbers. Drop self-references. |
| Auto-action button | `autoAction: true` |
| Initial button (highlighted on entry) | `defaultButton` |
| Button groups (4:3 / 16:9 / letterbox / pan-scan) | `rect` from the group that matches `canvas.aspect` (widescreen when there is a choice); the letterbox and pan-scan groups of a widescreen menu go in the button's `altRects`. |
| Highlight subpicture + colour/contrast per state | Render per-button PNGs with alpha: the `selected` colours → `states.selected.image`, the `action` colours → `states.activated.image`. A hard-edged look is authentic. Don't smooth it. |
| Button command resolving to title/chapter | `play { media, chapter }`, where `media` points to the file that holds that title |
| Button command resolving to a menu | `goto { menu, button? }` |
| Resume (`LinkRSM`), including a conditional one such as `if (gN != 0) LinkRSM` else a link elsewhere | `resume`; the path taken when there's nothing to resume becomes its `otherwise` (e.g. `goto` a widescreen/fullscreen picker), so the button works the first time too. |
| The disc's resume information (RSM) | `resumePoint: "untilUsed"`, so it lasts until a Resume uses it, as on the disc. A title's call to a menu (`CallSS` with a resume cell) is `saveResume` with that cell's time (no `media`: the playback in progress) before the `goto`. A jump out of a title that saves nothing (`JumpSS`, `JumpTT`) is `clearResume` before the `goto`, or nothing if the disc keeps an earlier point. |
| Title that plays other titles first (e.g. intro + episode) | `playAll { items }` |
| "Play all" titles (one PGC chaining episodes) | `playAll`, or `play` of the combined file if one was written |
| Button text (e.g. from OCR) | `label`, and `a11yLabel` if the text is unhelpful |
| Buttons unreachable by arrows / tiny hidden buttons | Keep them. Put their media in a `fallback` group with `hidden: true`. |
| Titles and chapters (PTT) | `media` entries with `chapters` (start time in seconds within the output file) |
| Remote Return (Go Up) | Menu `back`, chosen per menu to behave like the disc: the PGC's **Go Up** target when it has one (`goto` that menu); otherwise the menu's own **on-screen back button**, i.e. a button that returns to the menu this one was entered from (`activate` it, so its highlight shows and the viewer lands on the right item); otherwise leave `back` out (history). |
| Highlight information that changes during a menu cell (new HLI start/end times in the menu's navigation packets) | `buttonSets` on the menu: one set per run of the cell with the same buttons, `from`/`to` on the menu video's clock; the menu's own `buttons` are the ones it shows longest. |
| Menu that moves on by itself (still time or end of the menu's video, then a link) | If the next part has the same buttons, it's the same menu looping: `background.video` with `loopStart`. Otherwise menu `timeout` with `after` = the time until it moves on and the link as its `action`; `restartOnInput: false` to match the disc's timer exactly. |
| Next / previous page (`LinkNextPG`, `LinkPrevPG`, PGC next/prev) | `goto` with `history: "replace"`, so paging adds no Back steps. |
| General registers (GPRM 0–15) | Manifest `vars` (integers). Registers in counter mode (SetGPRMMD) are also listed in `counters`. |
| Temporary parental level (SetTmpPML level, line) | `requestParental` with `level`; `then` is the program from that line, `else` what follows the command. |
| Parental blocks (PGCs chosen by the player's parental level) | `if` on `@parentalLevel` choosing the play. |
| Navigation timer (SetNVTMR; SPRM 9 and 10) | `timer` with `after` = the seconds and `action` = a `run` of the PGC it names (a `play` of that title); a time of 0 is `cancelTimer`. SPRM 9 reads as `@navTimer`. |
| Register commands (Set: mov, add, sub, mul, div, mod, and, or, xor, rnd) | `set` with the matching `op` (`rnd` → `random`). |
| Conditional commands (`if (a op b)`), Goto/Break programs | `if`, `do`; a program's skipped lines become `else` branches. |
| Player settings read by commands (SPRM) | Player state: SPRM 1/2/3 → `@audio`/`@subtitle`/`@angle` (SPRM 2 is off when bit 6 is clear), 8 → `@focus` (button number ÷ 1024 → its id), 0/16/18 → `@menuLanguage`/`@audioLanguage`/`@subtitleLanguage` (ISO 639 codes as text), 4–7 → `@lastMedia`/`@lastChapter`, 12 → `@country`, 13 → `@parentalLevel`, 14 → `@aspect`, 20 → `@region`. |
| SetSTN (choose audio/subtitle/angle) | `set` on `@audio`/`@subtitle`/`@angle`. In a cell command or a button over the film, it changes the playing title's track at once. |
| Multi-angle title (angle blocks in its cells) | Media `angles`: one file per angle, as the rip wrote them (each whole title, the angle blocks differing). Or `angleTracks` when the rip keeps the angles as video tracks of one file. Menus that choose an angle with SetSTN set `@angle`. |
| Title that ends at a cell (a cell command `LinkTailPGC`/`Exit`, e.g. one song of a concert) | Play item `end` at that cell's end time. |
| Other cell commands during a title (SetGPRM, CallSS to a menu, conditional links) | Point cues `{ at, action }` at the cell's end time: `set`, `goto`, `if`. Jumps within the title (LinkCN, LinkPGN, LinkPTTN, a cell linking to its own start to loop) are `seek` to that cell's time on the title's timeline. |
| Still cells (a cell's still time, 1–254 seconds or 255 = until the viewer goes on; a PGC's still time after its last cell) | `holds` on the media (or the play item, when only one way of playing it holds): `{ at, for }` at the cell's last frame on the title's timeline, `for` the seconds or `"input"`. |
| Menus made of title cells (a still or short cell whose buttons wait for a choice) | An overlay with the cell's buttons over the cell's time, and a hold `"input"` at its last frame; a cell that loops instead is a point cue at its end that `seek`s back to its start. |
| Buttons in a title's navigation packets (highlights shown over the film) | Overlays `{ from, to, buttons }` on the media, one per run of VOBUs with the same buttons; a button that calls a featurette (and resumes with RSM) is a `play` from the overlay, which returns to the film. |
| Titles that share video, or play it out of order (a play-all and its episodes, songs of a concert, cuts of a film; seamless branching's interleaved cells) | Either one file per title as the rip made them, or, to keep one copy of shared video, a media with `segments` per title over the ranges of the files that hold each run of cells. Chapters and cues then count on the title's own timeline. |
| Shared programs: PGCs many links lead into (their pre-commands), menu-domain dispatchers, title post-commands, loops (a Goto back to an earlier line) | Named `actions`, each written once and reached with `run`. A loop is a named action that runs itself in tail position. Keeps the export about the size of the disc's own programs. |
| The remote's Menu and Title keys, and menu calls (MenuCall: root, title, audio, subpicture, angle, PTT) | `keys`: `menu`, `title`, `audioMenu`, `subtitleMenu`, `angleMenu`, `chapterMenu`. During a title, the title set's menu of that type (its VTSM entry), on the media; in menus, on the manifest (VMG title menu) and per title set's menus. |
| User operation prohibitions (UOPs, on PGCs and in navigation packets) | `prohibits` on the menu, media or play item: UOP 0/5 → `seek`, 1 → `chapterJump`, 2 → `titleJump`, 3 → `stop`, 4 → `back`, 6 → `previous`, 7 → `next`, 8 → `fastForward`, 9 → `rewind`, 10 → `titleKey`, 11 → `menuKey`, 12 → `subtitleMenuKey`, 13 → `audioMenuKey`, 14 → `angleMenuKey`, 15 → `chapterMenuKey`, 16 → `resume`, 17 → `buttons`, 18 → `endHold`, 19 → `pause`, 20 → `audioChange`, 21 → `subtitleChange`, 22 → `angleChange`, 23 → `karaokeChange`, 24 → `displayModeChange`. Information only: players decide. |
| Exit | `exit`. |
| PGC playback mode: random or shuffle (with a program count) | `playAll` with `order: "random"` and `count`, or `order: "shuffle"`. |
| Karaoke mixing (SetAMXMD, SPRM 11) | `set` on `@karaoke`. |
| Menu language (SPRM 0, the player's setup) | `@menuLanguage`, which the player reports from its settings; menus per language as below. |
| SetHL_BTN (highlight a button) | `set` on `@focus`, or `goto` with `button`. |
| Menus per language (several language units in a menu table) | One menu per language; `firstPlay` (or the link into them) chooses with `if` on `@menuLanguage`. |
| Anything not expressible yet | `x-dvd` on the menu or button: VTS/PGC/cell/button numbers, raw VM commands, register conditions |

## Suggested `x-dvd` shape

`x-dvd` is informational. Players ignore it. Keeping it lets future versions, or full DVD-VM players, replay the disc more faithfully, and it lets an export be traced back to the disc.

```json
"x-dvd": { "domain": "VTSM", "vtsn": 1, "pgcn": 2, "cell": 1 }
```

On a button:

```json
"x-dvd": { "button": 3, "colorTable": 0, "commands": ["SetSTN (audio 1)", "JumpVTS_PTT 1:1"] }
```

## Not supported yet

Every feature found in an audit of 954 discs now has a manifest form. Importers still keep the disc's
own numbers in `x-dvd` (above) so an export can be traced back to the disc.

## Assets

- **Images:** PNG. Backgrounds MAY be JPEG.
- **Motion menus** (Level 2): H.264 MP4 or VP9 WebM loops.
- **Menu audio** (Level 2): AAC or Opus.
- Keep a menu's assets small (a few MB at most) so menus open instantly on TVs over Wi-Fi.

## Worked example

[`examples/dvd-shaped`](../../examples/dvd-shaped) is a synthetic title built the way this profile describes. Conformance case [`07-dvd-shaped.json`](../../conformance/cases/07-dvd-shaped.json) checks the same shape.
