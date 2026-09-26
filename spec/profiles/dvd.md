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

## Assets

- **Images:** PNG. Backgrounds MAY be JPEG.
- **Motion menus** (Level 2): H.264 MP4 or VP9 WebM loops.
- **Menu audio** (Level 2): AAC or Opus.
- Keep a menu's assets small (a few MB at most) so menus open instantly on TVs over Wi-Fi.

## Worked example

[`examples/dvd-shaped`](../../examples/dvd-shaped) is a synthetic title built the way this profile describes. Conformance case [`07-dvd-shaped.json`](../../conformance/cases/07-dvd-shaped.json) checks the same shape.
