# DVD import profile (early draft)

This profile describes how a tool that reads a DVD's menu structure (from an unencrypted disc image or VIDEO_TS folder) should express it as a manifest, so any importer produces files every player understands. It covers Level 1 today. Level 2 items (motion menus, timeouts, register logic, audio/subtitle setup) are listed so importers can keep that data in `x-dvd` until the core supports it.

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
| Button groups (4:3 / 16:9 / letterbox / pan-scan) | Use the group that matches `canvas.aspect`. Prefer widescreen when there is a choice. |
| Highlight subpicture + colour/contrast per state | Render per-button PNGs with alpha: the `selected` colours → `states.selected.image`, the `action` colours → `states.activated.image`. A hard-edged look is authentic. Don't smooth it. |
| Button command resolving to title/chapter | `play { media, chapter }`, where `media` points to the file that holds that title |
| Button command resolving to a menu | `goto { menu, button? }` |
| Title that plays other titles first (e.g. intro + episode) | `playAll { items }` |
| "Play all" titles (one PGC chaining episodes) | `playAll`, or `play` of the combined file if one was written |
| Button text (e.g. from OCR) | `label`, and `a11yLabel` if the text is unhelpful |
| Buttons unreachable by arrows / tiny hidden buttons | Keep them. Put their media in a `fallback` group with `hidden: true`. |
| Titles and chapters (PTT) | `media` entries with `chapters` (start time in seconds within the output file) |
| Remote Return (Go Up) | Menu `back`, chosen per menu to behave like the disc: the PGC's **Go Up** target when it has one (`goto` that menu); otherwise the menu's own **on-screen back button**, i.e. a button that returns to the menu this one was entered from (`activate` it, so its highlight shows and the viewer lands on the right item); otherwise leave `back` out (history). |
| Next / previous page (`LinkNextPG`, `LinkPrevPG`, PGC next/prev) | `goto` with `history: "replace"`, so paging adds no Back steps. |
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

These disc features have no manifest form yet. Importers should keep what they know in `x-dvd`, label the affected buttons honestly, and play the film normally instead.

- **Buttons during playback** (e.g. *The Matrix*'s "Follow the White Rabbit": an icon appears in certain scenes, Enter plays a short featurette, then the film resumes). The planned shape is a timed button on the media ("from 41:10 to 41:25, activating plays X, then resumes at 41:25"). This is a planned Level 2 feature.
- **Seamless branching** (multi-path titles such as a theatrical and an extended cut sharing segments). The format can express this as a playlist of time ranges from one file, and players can join them gaplessly. The usual obstacle is the rip: many rips follow one path through the disc, so the other path's segments aren't in the file. A branching manifest needs a rip that keeps every segment. This is planned for a later draft.
- **Multi-angle video**, which relies on the rip keeping every angle.

## Assets

- **Images:** PNG. Backgrounds MAY be JPEG.
- **Motion menus** (Level 2): H.264 MP4 or VP9 WebM loops.
- **Menu audio** (Level 2): AAC or Opus.
- Keep a menu's assets small (a few MB at most) so menus open instantly on TVs over Wi-Fi.

## Worked example

[`examples/dvd-shaped`](../../examples/dvd-shaped) is a synthetic title built the way this profile describes. Conformance case [`07-dvd-shaped.json`](../../conformance/cases/07-dvd-shaped.json) checks the same shape.
