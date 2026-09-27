# MenuStream manifest format — v0.2 (draft)

> **Status: draft.** Fields and names may change before v1.0. "MenuStream" is a working name.
> After v1.0 the [compatibility charter](COMPATIBILITY.md) applies and nothing here is ever removed or redefined.

**v0.2** adds motion menus (§5.1), transitions (§6), variables with audio/subtitle selection (§6.1), and menu-defined Back (§6.2). Every v0.1 manifest is a valid v0.2 manifest and behaves the same.

The key words MUST, MUST NOT, SHOULD, SHOULD NOT and MAY are used as described in RFC 2119.

## 1. Purpose

A MenuStream manifest adds a disc-style menu to media that already exists: a film and its extras, a season of episodes, an imported DVD. It holds backgrounds, buttons, highlight art, remote-control navigation and actions. Every action **points to** existing media files or streams. The manifest never contains video.

## 2. Files

- A manifest is a UTF-8 JSON file named `<Title>.menustream.json`, placed next to the main media file, for example `Sintel (2010)/Sintel (2010).menustream.json`.
- **Relative paths resolve against the manifest's own URL or location.** They use forward slashes, and they MAY contain spaces and other characters (readers percent-encode as needed).
- Absolute URLs are allowed anywhere a path is.
- **Keep menu art away from media scanners.** Put menu assets in their own folder (e.g. `menus/`). If it holds video or audio, include a `.plexignore` containing `*` and an empty `.ignore` (Jellyfin), or media servers will list the clips as titles or as extra versions of the film. `menustream validate` warns when these are missing.
- *(Reserved)* A bundle `<Title>.msz` is a zip archive containing `manifest.json` and its assets. Bundles are specified in a later draft.

The machine-readable schema is [`schema/manifest.schema.json`](schema/manifest.schema.json). The schema checks structure only. Cross-references (§7) are checked by validators such as `menustream validate`.

## 3. Top level

| Field | Required | Meaning |
|---|---|---|
| `format` | yes | Always `"menustream"`. |
| `version` | yes | `"MAJOR.MINOR"`. This document is `0.2`; `0.1` manifests remain valid. |
| `title` | yes | `{ name, year?, ids? }`. `ids` holds strings such as `imdb`, `tmdb` or `tvdb`. |
| `source` | no | `{ kind, generator? }`. `kind` is e.g. `"dvd"`, `"authored"` or `"generated"`. |
| `media` | yes | Map of media id → [media](#4-media). At least one entry. |
| `menus` | no | Map of menu id → [menu](#5-menus). |
| `rootMenu` | if `menus` | The menu opened at start and after top-level playback ends. |
| `firstPlay` | no | [Action](#6-actions) run at start instead of opening `rootMenu`, e.g. an intro. |
| `vars` | no | *(v0.2, Level 2)* Named integer variables with starting values, e.g. `{"audio": 1, "subs": 0}` (§6.1). |
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
- **`buttonsAt`** *(v0.2)*: see §5.1.
- **`defaultButton`** gets focus when the menu opens. It defaults to the first button.
- **`back`** *(v0.2)*: what the remote's Back key does on this menu (§6.2).
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

### 5.1 Motion menus (v0.2)

```json
"background": {
  "image": "menus/main.jpg",
  "video": { "src": "menus/main-loop.mp4", "loopStart": 2.5 },
  "audio": { "src": "menus/menu-music.m4a" }
},
"buttonsAt": 2.5
```

- **`background.video`** is a motion background stretched to the canvas like `image`. It plays once from 0, which is the menu's *intro*, then loops from `loopStart` (default 0) for as long as the menu is shown. It MAY carry its own audio.
- **`background.audio`** is menu music. It loops the same way and plays alongside the video.
- **`image`** remains the poster: shown while the video loads, and by players without motion support. Those players still work, because they simply show the still.
- **`buttonsAt`** is the number of seconds into the intro before buttons appear and respond. It's measured on the background video's clock when there is one, otherwise from when the menu opened. Until then, buttons and their highlights are hidden.
- **Intros play only on a fresh entry:** at start, or via `goto`. Returning to a menu with `back` or at the end of playback starts the background at `loopStart` with buttons ready, so the intro is not replayed.
- **Any key or click during an intro skips it.** Players jump the background to `buttonsAt` and show the buttons. That input does nothing else. Pointer *hover* does not skip. Players MUST NOT make intros unskippable.
- **Keep menu assets small** (a few MB per menu) so menus open instantly on TVs over Wi-Fi. H.264 MP4 or VP9 WebM for video; AAC (in MP4/M4A) or Opus for audio. MP4/M4A files SHOULD be written with the index at the start ("faststart"), because some players, such as Android WebView, stall on files that must be read from the end first.

## 6. Actions

Every action is an object with a `type`:

| `type` | Fields | Behaviour |
|---|---|---|
| `play` | `media`, `chapter?`, `start?`, `after?` | Play one media item. `chapter` (1-based) wins over `start` (seconds). |
| `playAll` | `items[]`, `after?` | Play `{ media, chapter?, start? }` items in order. |
| `goto` | `menu`, `button?`, `transition?`, `history?` | Open a menu, focusing `button` or its `defaultButton`. *(v0.2)* `transition` is a clip (path) played first, full screen. *(v0.2)* `history: "replace"` doesn't add the current menu to Back's history, for page turns (§6.2). |
| `back` | — | Return to the previous menu in the history. At the top of the history, the player MAY exit. |
| `activate` | `button` | *(v0.2)* Press a button of the current menu: its activated state shows and its action runs. Only allowed in a menu's `back` (§6.2). |
| `set` | `var`, `value`, `then?` | *(v0.2, Level 2)* Set a variable, then optionally run another action (§6.1). |
| `resume` | — | Continue the last playback the viewer stopped early, from where they stopped. Otherwise it does nothing. |

**Navigation semantics** (normative; the conformance suite checks them):
1. **Start:** if `firstPlay` exists, run it. Otherwise open `rootMenu`. A manifest without menus shows the fallback list.
2. **`goto` from a button** pushes the current menu and focused button onto the history (unless it says `history: "replace"`). `back` pops it.
3. **When playback completes on its own**, the player returns to the menu and button it was started from, then runs `after` if present. Playback started with no menu, for example from `firstPlay`, returns to `rootMenu`.
4. **When the viewer stops playback early** (the Back key), the player returns to the same place and does not run `after`. It remembers the media and position for `resume`.
5. **During playback**, direction keys and Enter belong to the video player. Only Back is handled by the menu logic.
6. **`after`** MUST NOT be `play` or `playAll`. Use `playAll` for sequences.
7. *(v0.2)* **Transitions:** a `goto` with `transition` pushes history as usual, plays the clip, then opens the target menu as a fresh entry (with its intro). **Any key or click skips the transition** straight to the menu. Hover is ignored.

### 6.1 Variables and track selection (v0.2, Level 2)

Discs remember choices, such as the soundtrack picked in a Languages menu, in registers. MenuStream keeps them as **variables**: data, not scripts.

```json
"vars": { "audio": 1, "subs": 0 },
...
{ "type": "set", "var": "audio", "value": 2, "then": { "type": "goto", "menu": "main" } }
...
{ "type": "play", "media": "feature", "audio": { "var": "audio" }, "subtitle": { "var": "subs" } }
```

- **Session scope.** Variables start at their `vars` values when the player starts, and keep their values across menus and playback until it starts again.
- **`set`** assigns an integer to a declared variable, then runs `then` if present. `set` MUST NOT be used in the fallback list.
- **Choosing tracks.** `play` and `playAll` items MAY have `audio` and `subtitle`, each either a number or `{"var": name}`:
  - `audio: N` plays the Nth audio track of the media (1-based).
  - `subtitle: N` shows the Nth subtitle track; `subtitle: 0` turns subtitles off.
  - When absent, the player uses its default. A number outside the media's tracks is ignored.
- **Players without Level 2** ignore these fields: the media plays with default tracks, and `set` actions still run their `then`.
- Conditions on actions (`when`) are planned for a later draft.

### 6.2 The remote's Back key (v0.2)

On a disc, the remote's Return key doesn't retrace every menu you visited: each menu has a "go up" target. MenuStream menus say the same with `back`:

```json
"neo-bio-2": {
  "back": { "type": "activate", "button": "cast" },
  "buttons": [
    { "id": "cast", "rect": [300, 400, 100, 40], "action": { "type": "goto", "menu": "cast", "button": "neo" } },
    { "id": "next", "rect": [500, 400, 100, 40], "action": { "type": "goto", "menu": "neo-bio-3", "history": "replace" } }
  ]
}
```

- **Remote Back on a menu with `back`** runs that action instead of popping history. It MAY be any action; `activate` presses one of the menu's own buttons (typically its on-screen Back or "Cast" button), showing its activated state.
- **It is a step up, not a new visit.** A `goto` run by `back` doesn't add to history, and if its target is already in the history, the history is unwound to it (that entry and everything after it are dropped). So ten bio pages still take one press to leave.
- **Page turns** (next/previous page of the same thing) SHOULD use `history: "replace"`, so they add no Back steps even in menus without `back`.
- **Without `back`**, Back returns through history as in §6 (at the top, the player MAY exit).
- **Back never traps the viewer.** If a menu's `back` leaves the viewer on the same menu, players MUST return through history instead. (A disc may leave Return inactive; a TV app must not.)
- **Importers choose per menu** to match the source as closely as possible: a disc's Go Up target, the menu's own on-screen Back button, or history (see the [DVD profile](profiles/dvd.md)).
- Players without v0.2 ignore `back` and `history`, and Back returns through history.

## 7. Cross-reference rules

Validators MUST reject a manifest when:
- a `media`, `menu`, `rootMenu`, `defaultButton`, `goto.button`, `activate.button` or `nav` target doesn't exist, or
- `activate` is used anywhere but a menu's `back`, or
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
| **2: Disc** | *(v0.2, partial)* Motion backgrounds, menu audio, `buttonsAt`, transitions and skipping, variables with audio/subtitle selection. *(Next drafts)* Menu timeouts, conditions, per-language menus, buttons during playback, segment playlists for seamless branching (see [DVD profile: Not supported yet](profiles/dvd.md#not-supported-yet)). |

Players claim a level by passing that level's cases in [`conformance/`](../conformance). Each case lists input events and the expected state and effects, so any implementation in any language can run them.
