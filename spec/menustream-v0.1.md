# MenuStream manifest format — v0.1 (draft)

> **Status: draft.** Fields and names may change before v1.0. "MenuStream" is a working name.
> After v1.0 the [compatibility charter](COMPATIBILITY.md) applies and nothing here is ever removed or redefined.

The key words MUST, MUST NOT, SHOULD, SHOULD NOT and MAY are used as described in RFC 2119.

## 1. Purpose

A MenuStream manifest adds a disc-style menu to media that already exists: a film and its extras, a season of episodes, an imported DVD. It holds backgrounds, buttons, highlight art, remote-control navigation and actions. Every action **points to** existing media files or streams. The manifest never contains video.

## 2. Files

- A manifest is a UTF-8 JSON file named `<Title>.menustream.json`, placed next to the main media file, for example `Sintel (2010)/Sintel (2010).menustream.json`.
- **Relative paths resolve against the manifest's own URL or location.** They use forward slashes, and they MAY contain spaces and other characters (readers percent-encode as needed).
- Absolute URLs are allowed anywhere a path is.
- *(Reserved)* A bundle `<Title>.msz` is a zip archive containing `manifest.json` and its assets. Bundles are specified in a later draft.

The machine-readable schema is [`schema/manifest.schema.json`](schema/manifest.schema.json). The schema checks structure only. Cross-references (§7) are checked by validators such as `menustream validate`.

## 3. Top level

| Field | Required | Meaning |
|---|---|---|
| `format` | yes | Always `"menustream"`. |
| `version` | yes | `"MAJOR.MINOR"`. This document is `0.1`. |
| `title` | yes | `{ name, year?, ids? }`. `ids` holds strings such as `imdb`, `tmdb` or `tvdb`. |
| `source` | no | `{ kind, generator? }`. `kind` is e.g. `"dvd"`, `"authored"` or `"generated"`. |
| `media` | yes | Map of media id → [media](#4-media). At least one entry. |
| `menus` | no | Map of menu id → [menu](#5-menus). |
| `rootMenu` | if `menus` | The menu opened at start and after top-level playback ends. |
| `firstPlay` | no | [Action](#6-actions) run at start instead of opening `rootMenu`, e.g. an intro. |
| `fallback` | **yes** | [Fallback list](#8-fallback-list-level-0) for players without menu support. |
| `x-*` | no | Vendor extensions (§9). |

## 4. Media

```json
"feature": {
  "ref": "Sintel (2010).mp4",
  "kind": "feature",
  "label": "Sintel",
  "duration": 888,
  "chapters": [{ "time": 0, "label": "Chapter 1", "thumbnail": "menus/chapter-1.jpg" }]
}
```

- `ref` (required) is either:
  - a relative path or URL, or
  - `{ "scheme": "...", "id": "..." }`, an opaque reference the host player resolves. For example, a media server can use its own item ids. A player that doesn't know a scheme treats that media as unavailable.
- `kind` suggests how to group the media: `feature`, `episode`, `trailer`, `featurette`, `behindthescenes`, `deleted`, `interview`, `scene`, `short` or `other`. Other values are allowed.
- `chapters` are start times in seconds and MUST increase. Chapter numbers in actions start at **1**.

## 5. Menus

```json
"main": {
  "canvas": { "width": 720, "height": 480, "aspect": "16:9" },
  "background": { "image": "menus/main.png", "color": "#000" },
  "defaultButton": "play",
  "autoNav": false,
  "buttons": [ ... ]
}
```

- **`canvas`** sets the coordinate space for every rect in the menu, in storage pixels.
  - `aspect` is the display aspect ratio (`"16:9"`, `"4:3"`, `"2.39:1"` or a number). It defaults to `width/height`, meaning square pixels.
  - Players MUST display the canvas at `aspect`, scaled to fit the screen and letterboxed or pillarboxed as needed. This is how anamorphic DVD menus (720×480 shown at 16:9) keep correct geometry.
- **`background.image`** is stretched to the whole canvas. `background.color` fills anything not covered.
- **`defaultButton`** gets focus when the menu opens. It defaults to the first button.
- **`autoNav: true`** means that pressing a direction with no `nav` entry moves to the nearest button that way. The reference algorithm uses button centres: a candidate must lie in the direction of travel, and the score is the distance along the axis plus twice the sideways offset. With `autoNav` false or absent, a missing direction does nothing, which matches DVD behaviour.

### Buttons

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Unique within the menu. |
| `rect` | yes | `[x, y, width, height]` in canvas pixels. |
| `action` | yes | What activating the button does. |
| `label` | no | Human name. Used for accessibility and tools, and drawn only when `showLabel` is true. |
| `a11yLabel` | no | Screen-reader text when it differs from `label`. |
| `showLabel` | no | If true, the player draws `label` itself. Otherwise the text is part of the background art, as on discs. |
| `states` | no | `normal`, `selected` and `activated`, each `{ image?, rect?, textColor?, fill? }`. |
| `nav` | no | `{ up?, down?, left?, right? }`: button ids in the same menu. |
| `autoAction` | no | If true, the button activates as soon as it is selected (DVD auto-action). |

**How states are drawn:**
- The state's `image` is drawn over `rect`, or over the state's own `rect` if it has one. PNG with alpha is recommended.
- Players show exactly one state per button: `activated` briefly when the button is activated, `selected` while it has focus, otherwise `normal`.
- If a button has no `selected` image and no drawn label, players SHOULD draw their own focus indicator, so focus is never invisible.

## 6. Actions

Every action is an object with a `type`:

| `type` | Fields | Behaviour |
|---|---|---|
| `play` | `media`, `chapter?`, `start?`, `after?` | Play one media item. `chapter` (1-based) wins over `start` (seconds). |
| `playAll` | `items[]`, `after?` | Play `{ media, chapter?, start? }` items in order. |
| `goto` | `menu`, `button?` | Open a menu, focusing `button` or its `defaultButton`. |
| `back` | — | Return to the previous menu in the history. At the top of the history, the player MAY exit. |
| `resume` | — | Continue the last playback the viewer stopped early, from where they stopped. Otherwise it does nothing. |

**Navigation semantics** (normative; the conformance suite checks them):
1. **Start:** if `firstPlay` exists, run it. Otherwise open `rootMenu`. A manifest without menus shows the fallback list.
2. **`goto` from a button** pushes the current menu and focused button onto the history. `back` pops it.
3. **When playback completes on its own**, the player returns to the menu and button it was started from, then runs `after` if present. Playback started with no menu, for example from `firstPlay`, returns to `rootMenu`.
4. **When the viewer stops playback early** (the Back key), the player returns to the same place and does not run `after`. It remembers the media and position for `resume`.
5. **During playback**, direction keys and Enter belong to the video player. Only Back is handled by the menu logic.
6. **`after`** MUST NOT be `play` or `playAll`. Use `playAll` for sequences.

## 7. Cross-reference rules

Validators MUST reject a manifest when:
- a `media`, `menu`, `rootMenu`, `defaultButton`, `goto.button` or `nav` target doesn't exist, or
- button ids are duplicated within a menu, or
- a chapter number is out of range, or
- `fallback` is empty or all of it is hidden.

Rects that extend outside the canvas are warnings, not errors.

## 8. Fallback list (Level 0)

```json
"fallback": [
  { "label": "Movie", "items": [{ "label": "Play Movie", "action": { "type": "play", "media": "feature" } }] },
  { "label": "Hidden features", "hidden": true, "items": [ ... ] }
]
```

Every manifest MUST include a fallback list: groups of items whose actions are `play` or `playAll`. A player that can only show a list and play media supports every manifest ever written. Groups marked `hidden` (easter eggs) are not shown unless the viewer asks for them.

## 9. Unknown fields and extensions

- Readers MUST ignore properties they don't understand.
- Vendor or tool data goes in properties starting with `x-`, e.g. `x-dvd` for disc provenance (see [profiles/dvd.md](profiles/dvd.md)). Validators SHOULD warn about unknown properties that don't start with `x-`, because they're usually typos.
- A manifest with a **newer minor version** MUST still be read: its unknown fields are ignored.
- A manifest with a **different major version** MUST be rejected rather than guessed at.

## 10. Conformance levels

| Level | A player must support |
|---|---|
| **0: Basic** | The fallback list and playback. |
| **1: Menus** | Everything in this document: canvases and aspect, backgrounds, buttons, state overlays, nav/autoNav, autoAction, chapters, all actions, and the navigation semantics. |
| **2: Disc** | *(Next draft)* Motion and audio loops, menu timeouts, variables and conditions, audio/subtitle selection, per-language menus. |

Players claim a level by passing that level's cases in [`conformance/`](../conformance). Each case lists input events and the expected state and effects, so any implementation in any language can run them.
