# Open Video Menu manifest format — v0.1 (draft)

> **Status: draft.** Fields may change before v1.0.
> After v1.0 the [compatibility charter](COMPATIBILITY.md) applies and nothing here is ever removed or redefined.

The key words MUST, MUST NOT, SHOULD, SHOULD NOT and MAY are used as described in RFC 2119.

## 1. Purpose

An Open Video Menu manifest adds a menu to media that already exists: a film and its extras, a season of episodes, an imported DVD. It holds backgrounds, buttons, highlight art, remote-control navigation and actions. Every action **points to** existing media files or streams. The manifest never contains video.

## 2. Files

- A manifest is a UTF-8 JSON file named `<Title>.openvideomenu.json`, placed next to the main media file, for example `Sintel (2010)/Sintel (2010).openvideomenu.json`.
- **Relative paths resolve against the manifest's own URL or location.** They use forward slashes, and they MAY contain spaces and other characters (readers percent-encode as needed).
- Absolute URLs are allowed anywhere a path is.
- **Keep menu art away from media scanners.** Put menu assets in their own folder (e.g. `menus/`). If it holds video or audio, include a `.plexignore` containing `*` and an empty `.ignore` (Jellyfin), or media servers will list the clips as titles or as extra versions of the film. `openvideomenu validate` warns when these are missing.

### 2.1 Bundles

A menu set can ship as one file instead of a manifest plus a folder of art: a **bundle** named
`<Title>.ovmz`, placed where the manifest would be (next to the main media file).

- **Publishers SHOULD bundle manifests that have local assets** (menu images, clips, audio), so a title's
  folder holds one menu file. A plain `<Title>.openvideomenu.json` with loose assets remains valid.
  Manifests without local assets need no bundle.
- **A bundle is a ZIP archive** holding `manifest.json` at its root and the manifest's assets at the
  paths the manifest names (e.g. `menus/main.jpg`).
- **Every entry MUST be stored uncompressed** (ZIP method 0), and entries MUST NOT be encrypted. Menu
  art is already compressed (JPEG, PNG, video, audio), so compression would save almost nothing, while
  stored entries let a server serve any member, including byte ranges of a video clip, without
  unpacking. ZIP64 MAY be used for bundles over 4 GB.
- **Entry names** use forward slashes and MUST NOT be absolute or contain `..` segments.
- **Paths resolve as if the bundle were a folder** named `<Title>.ovmz` holding `manifest.json`: asset
  paths resolve inside the bundle. **`media` refs resolve against the folder that contains the bundle**,
  exactly as for a plain manifest, so the same manifest works in either form.
- If a folder holds both `<Title>.ovmz` and `<Title>.openvideomenu.json`, readers SHOULD use the bundle.
- Readers MUST support both forms. `openvideomenu pack` makes a bundle from a manifest and its assets;
  `openvideomenu unpack` does the reverse.

The machine-readable schema is [`schema/manifest.schema.json`](schema/manifest.schema.json). The schema checks structure only. Cross-references (§7) are checked by validators such as `openvideomenu validate`.

## 3. Top level

| Field | Required | Meaning |
|---|---|---|
| `format` | yes | Always `"openvideomenu"`. |
| `version` | yes | `"MAJOR.MINOR"`. This document is `0.1`. |
| `title` | yes | `{ name, year?, ids? }`. `ids` holds strings such as `imdb`, `tmdb` or `tvdb`. |
| `source` | no | `{ kind, generator? }`. `kind` is e.g. `"dvd"`, `"authored"` or `"generated"`. |
| `media` | yes | Map of media id → [media](#4-media). At least one entry. |
| `menus` | no | Map of menu id → [menu](#5-menus). |
| `rootMenu` | if `menus` | The menu opened at start and after top-level playback ends. |
| `firstPlay` | no | [Action](#6-actions) run at start instead of opening `rootMenu`, e.g. an intro. |
| `vars` | no | *(Level 2)* Named integer variables with starting values, e.g. `{"audio": 1, "subs": 0}` (§6.1). |
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

### 4.1 Angles (Level 2)

The same scene filmed or drawn more than one way: a DVD's multi-angle title, a sing-along with and
without lyrics, a storyboard beside the finished shot. A media entry says where its angles are in one
of two ways, the author's choice (not both):

```json
"feature": { "ref": "Roots (angle 1).mkv", "angles": ["Roots (angle 2).mkv"] }
"storyboard": { "ref": "Storyboard.mkv", "angleTracks": 3 }
```

- **`angles`**: the other angles as separate files, each a `ref` (path, URL or `{scheme, id}`). The
  media's own `ref` is angle 1, `angles[0]` angle 2, and so on. The files MUST line up in time, so
  changing angle keeps the position. This is how rips of multi-angle titles come out: one whole file per
  angle.
- **`angleTracks`**: N (at least 2): the file's first N video tracks are angles 1 to N.

Which angle plays: the play item's `angle` (a number or `{"var": name}`, like `audio`), otherwise
`@angle` (§6.1). A number outside the media's angles plays angle 1, like a DVD player. A player SHOULD
let the viewer change angle during playback (a DVD remote's Angle key) and report it as `@angle`. A
player that can't switch video tracks plays angle 1 of `angleTracks` media. Players without Level 2
play the `ref` (angle 1).

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
- **`buttonsAt`**: see §5.1.
- **`defaultButton`** gets focus when the menu opens. It defaults to the first button.
- **`back`**: what the remote's Back key does on this menu (§6.2).
- **`timeout`**: what happens when the menu is left alone (§5.2).
- **`autoNav: true`** means that pressing a direction with no `nav` entry moves to the nearest button that way. The reference algorithm uses button centres: a candidate must lie in the direction of travel, and the score is the distance along the axis plus twice the sideways offset. With `autoNav` false or absent, a missing direction does nothing, which matches DVD behaviour.

### Buttons

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Unique within the menu. |
| `rect` | yes | `[x, y, width, height]` in canvas pixels. |
| `action` | yes | What activating the button does. |
| `label` | no | Human name. Used for accessibility and tools, and drawn only when `showLabel` is true. |
| `a11yLabel` | no | Screen-reader text when it differs from `label`. |
| `showLabel` | no | If true, the player draws `label` itself. Otherwise the text is part of the background art, as on DVDs. |
| `states` | no | `normal`, `selected` and `activated`, each `{ image?, rect?, textColor?, fill? }`. |
| `nav` | no | `{ up?, down?, left?, right? }`: button ids in the same menu. |
| `autoAction` | no | If true, the button activates as soon as it is selected (DVD auto-action). |

**How states are drawn:**
- The state's `image` is drawn over `rect`, or over the state's own `rect` if it has one. PNG with alpha is recommended.
- Players show exactly one state per button: `activated` briefly when the button is activated, `selected` while it has focus, otherwise `normal`.
- If a button has no `selected` image and no drawn label, players SHOULD draw their own focus indicator, so focus is never invisible.

### 5.1 Motion menus

```json
"background": {
  "image": "menus/main.jpg",
  "video": { "src": "menus/main-loop.mp4", "loopStart": 2.5 }
},
"buttonsAt": 2.5
```

- **`background.video`** is a motion background stretched to the canvas like `image`. It plays once from 0, which is the menu's *intro*, then loops from `loopStart` (default 0) for as long as the menu is shown. It MAY carry its own audio.
- **`background.audio`** is menu music. It loops the same way and plays alongside the video.
- **Music that plays with a motion background SHOULD be in the video itself**, not in `background.audio`. Some players can play only one stream at a time (Roku is one), so they would drop a separate audio track. Use `background.audio` for menus with a still image, or for music that deliberately loops independently of the video. A player that can't play both plays the video.
- **`image`** remains the poster: shown while the video loads, and by players without motion support. Those players still work, because they simply show the still.
- **`buttonsAt`** is the number of seconds into the intro before buttons appear and respond. It's measured on the background video's clock when there is one, otherwise from when the menu opened. Until then, buttons and their highlights are hidden.
- **Intros play only on a fresh entry:** at start, or via `goto`. Returning to a menu with `back` or at the end of playback starts the background at `loopStart` with buttons ready, so the intro is not replayed.
- **Any key or click during an intro skips it.** Players jump the background to `buttonsAt` and show the buttons. That input does nothing else. Pointer *hover* does not skip. Players MUST NOT make intros unskippable.
- **Keep menu assets small** (a few MB per menu) so menus open instantly on TVs over Wi-Fi. H.264 MP4 or VP9 WebM for video; AAC (in MP4/M4A) or Opus for audio. MP4/M4A files SHOULD be written with the index at the start ("faststart"), because some players, such as Android WebView, stall on files that must be read from the end first.

### 5.2 Menu timeouts (Level 2)

A menu can move on by itself when it's left alone, as DVD menus do when their video or still time runs
out: an intro gives way to the main menu, or the film starts after a minute.

```json
"timeout": { "after": 30, "action": { "type": "play", "media": "feature" } }
```

- **`after`** is in seconds, counted from when the menu appears (intro included). It starts over every
  time the menu appears, whether fresh or returned to.
- **`restartOnInput`** (default `true`): a key press or click restarts the countdown, so nothing starts
  while the viewer is choosing. Importers set it to `false` to match a disc whose timer runs regardless.
- **`action`** runs when the time is up, as a *step up*, like a menu's `back` (§6.2): no history is
  added, and a `goto` to a menu already in history unwinds it. Nobody chose it, so Back never returns to
  an intro that moved on by itself. It MAY `activate` one of the menu's own buttons.
- **A menu's own video looping is not a timeout.** An intro that runs into its loop with the same buttons
  is one menu with `background.video.loopStart` (§5.1).
- **Navigation model:** whenever a menu with a timeout appears, and after input when `restartOnInput`
  applies (unless the viewer is leaving), the navigator emits `{ "type": "startTimer", "menu", "after" }`.
  The player keeps the clock and reports `{ "type": "timeout", "menu" }` when it runs out. A timeout for a
  menu that isn't showing any more is ignored. A new `startTimer` replaces the previous countdown.

## 6. Actions

Every action is an object with a `type`:

| `type` | Fields | Behaviour |
|---|---|---|
| `play` | `media`, `chapter?`, `start?`, `end?`, `after?`, `cues?` | Play one media item. `chapter` (1-based) wins over `start` (seconds). *(Level 2)* `end` stops there as if the item ended; `cues` (§6.3). |
| `playAll` | `items[]`, `after?` | Play `{ media, chapter?, start? }` items in order. |
| `goto` | `menu`, `button?`, `transition?`, `history?` | Open a menu, focusing `button` or its `defaultButton`. `transition` is a clip (path) played first, full screen. `history: "replace"` doesn't add the current menu to Back's history, for page turns (§6.2). |
| `back` | — | Return to the previous menu in the history. At the top of the history, the player MAY exit. |
| `activate` | `button` | Press a button of the current menu: its activated state shows and its action runs. Only allowed in a menu's `back` (§6.2). |
| `set` | `var`, `value`, `op?`, `then?` | *(Level 2)* Change a variable or player state, then optionally run another action (§6.1). |
| `if` | `test`, `then`, `else?` | *(Level 2)* Run `then` or `else` depending on a condition (§6.1). |
| `do` | `steps` | *(Level 2)* Run actions in order until one navigates (§6.1). |
| `resume` | `otherwise?` | Continue the last playback the viewer stopped early, from where they stopped. When there's nothing to resume, run `otherwise` instead, as if it were the action itself (same history rules); without `otherwise`, nothing happens. `otherwise` is any action allowed where the `resume` is, but not another `resume`. |

**Navigation semantics** (normative; the conformance suite checks them):
1. **Start:** if `firstPlay` exists, run it. Otherwise open `rootMenu`. A manifest without menus shows the fallback list.
2. **`goto` from a button** pushes the current menu and focused button onto the history (unless it says `history: "replace"`). `back` pops it.
3. **When playback completes on its own**, the player returns to the menu and button it was started from, then runs `after` if present. Playback started with no menu, for example from `firstPlay`, returns to `rootMenu`.
4. **When the viewer stops playback early** (the Back key), the player returns to the same place and does not run `after`. It remembers the media and position for `resume`.
5. **During playback**, direction keys and Enter belong to the video player. Only Back is handled by the menu logic, except while an overlay's buttons show (§6.3).
6. **`after`** MUST NOT be `play` or `playAll`, and neither may the `otherwise` of a `resume` used as `after`. Use `playAll` for sequences.
7. **Transitions:** a `goto` with `transition` pushes history as usual, plays the clip, then opens the target menu as a fresh entry (with its intro). **Any key or click skips the transition** straight to the menu. Hover is ignored.

### 6.1 Variables and logic (Level 2)

DVDs keep state in registers and decide with small command programs: which soundtrack was chosen,
whether the film was started, the viewer's preferred language, which button to highlight. Open Video
Menu expresses the same logic as **data, not scripts**: variables, player state, conditions and a
few actions. It works on any media, not only discs, and the fallback list still works everywhere.

```json
"vars": { "audio": 1, "played": 0, "lang": "en" },
...
{ "type": "if",
  "test": { "var": "@audioLanguage", "eq": "fr" },
  "then": { "type": "do", "steps": [
    { "type": "set", "var": "@audio", "value": 2 },
    { "type": "goto", "menu": "main-fr" } ] },
  "else": { "type": "goto", "menu": "main" } }
```

**Variables.** `vars` declares the manifest's own variables and their starting values: an integer or
text. A variable keeps that type. Variables start at their `vars` values when the player starts and keep
their values across menus and playback until it starts again. Names MUST NOT start with `@`.

**Player state** is read as `@` variables. Players report what they know when they start (and when it
changes, e.g. the viewer picks another soundtrack during playback); anything unknown reads as its default.

| Variable | Type | Default | Meaning |
|---|---|---|---|
| `@audio` | integer | 0 (unknown) | Current audio track, 1-based. Writable. |
| `@subtitle` | integer | 0 (off) | Current subtitle track, 1-based; 0 = off. Writable. |
| `@angle` | integer | 1 | Current angle, 1-based. Writable. |
| `@focus` | text | — | The highlighted button's id. Writable: setting it highlights that button of the current menu. |
| `@audioLanguage`, `@subtitleLanguage`, `@menuLanguage` | text | `""` | The viewer's preferred languages, ISO 639-1 (`"fr"`). |
| `@lastMedia` | text | `""` | Media id of the last playback. |
| `@lastChapter` | integer | 0 | Chapter (1-based) that playback started in, updated to where it stopped. |
| `@region` | integer | 0 (unknown) | Player region, 1–8. |
| `@parentalLevel` | integer | 8 (unrestricted) | Parental level, 1–8. |
| `@country` | text | `""` | ISO 3166-1 alpha-2. |
| `@aspect` | text | `"16:9"` | The TV's shape: `"16:9"` or `"4:3"`. |

**Values** are an integer, text, or `{"var": name}` (the current value of a variable or player state).

**Conditions** (`test`) compare one variable with a value, or combine conditions:
- `{ "var": v, "eq" | "ne": value }` for any type; `"lt" | "le" | "gt" | "ge"` for integers; `"has"`: true
  when any bit of the value is set in the variable (integers).
- `{ "all": [tests] }`, `{ "any": [tests] }`, `{ "not": test }`.
- Exactly one comparison per test. Comparing an integer with text is an error.

**Actions:**
- **`set`** `{ var, value, op?, then? }` changes a variable, then runs `then`. `op` is `set` (default),
  `add`, `sub`, `mul`, `div` (towards zero), `mod`, `and`, `or`, `xor`, or `random` (a whole number from
  1 to `value`). Division or modulo by zero leaves the variable unchanged. Only `set` works on text.
  Writable player state: setting `@audio`/`@subtitle`/`@angle` chooses the track for the next playback
  (a DVD's SetSTN); setting `@focus` highlights a button (SetHL_BTN).
- **`if`** `{ test, then, else? }` runs `then` when the test holds, otherwise `else` (or nothing).
- **`do`** `{ steps }` runs actions in order and stops after the first one that navigates: opens a menu,
  starts playback or a transition, or leaves. Like a DVD program, which ends at its link.
- `if`, `do` and `set` MUST NOT be used in the fallback list. `after` MAY be an `if` or `do` that plays
  (a disc's conditional play-all), but not a bare `play`/`playAll`.

**Focus from a variable.** A menu's `defaultButton` and a `goto`'s `button` MAY be `{"var": name}`
holding a button id. When it doesn't name a button of that menu, the usual default applies.

**Choosing tracks.** `play` and `playAll` items MAY have `audio` and `subtitle`, each a number or
`{"var": name}`:
- `audio: N` plays the Nth audio track of the media (1-based); `subtitle: N` shows the Nth subtitle
  track; `subtitle: 0` turns subtitles off.
- Without them, playback uses the current `@audio` and `@subtitle` when known, otherwise the player's
  default. A number outside the media's tracks is ignored.
- `angle: N` plays angle N of media that has angles (§4.1); without it, `@angle` decides.

**Randomness.** `random` draws from a generator the player seeds at start. The reference navigator uses
`seed = (seed × 1103515245 + 12345) mod 2³¹`, result `1 + seed mod value`, so conformance cases can
check it; players seed it unpredictably.

**Players without Level 2** ignore these fields: menus open with their plain `defaultButton`, media play
with default tracks, and `set`/`if`/`do` do nothing. Authors who need Level 1 players to work should keep
important paths free of logic.

### 6.2 The remote's Back key

On a DVD, the remote's Return key doesn't retrace every menu you visited: each menu has a "go up" target. Open Video Menu menus say the same with `back`:

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
- **Back on `rootMenu` leaves the title** (the player exits), unless that menu has its own `back`. This holds however the viewer got there: a "return to the main menu" button is a `goto` and adds history, but the main menu is still the top, and retracing into a submenu from it surprises viewers.
- **Back never traps the viewer.** If a menu's `back` leaves the viewer on the same menu, players MUST return through history instead (or leave, on `rootMenu`). (A DVD may leave Return inactive; a TV app must not.)
- **Importers choose per menu** to match the source as closely as possible: a DVD's Go Up target, the menu's own on-screen Back button, or history (see the [DVD profile](profiles/dvd.md)).

### 6.3 During playback (Level 2)

Menus can reach into playback: stop a title at a point (a DVD's "play this song, then back to the
menu"), run an action when playback reaches a moment, or show buttons over the video, like the
icon some discs show in certain scenes that branches to a featurette and back.

```json
"feature": { "ref": "Film.mkv", "cues": [
  { "at": 1200, "action": { "type": "set", "var": "halfway", "value": 1 } },
  { "from": 2470, "to": 2485, "canvas": { "width": 1280, "height": 720 },
    "buttons": [{ "id": "follow", "rect": [1080, 600, 160, 90], "label": "Follow",
                  "states": { "normal": { "image": "menus/rabbit.png" } },
                  "action": { "type": "play", "media": "rabbit-1" } }] } ] }

{ "type": "play", "media": "concert", "start": 300, "end": 540 }
```

- **`end`** on a play item (seconds): playback stops there and counts as ended (`after` runs, a
  `playAll` goes on). It MUST be after `start`.
- **`cues`** on a media entry apply whenever it plays; on a play item, only when played that way,
  as well as the media's. Two kinds:
  - **Point cue** `{ at, action }`: the action runs when playback passes `at`. Not at the time
    playback starts from (nor on the way there, when a player starts at the keyframe before it),
    and not when the viewer seeks past it.
  - **Overlay** `{ from, to, canvas, buttons, defaultButton?, autoNav? }`: buttons over the video from
    `from` to `to`, with everything a menu's buttons have (states, labels, `nav`). While it shows, the
    arrows move between its buttons and Enter presses one; Back still stops playback. Overlays in one
    list MUST NOT overlap; when a media's and an item's overlap, the one that started last shows.
- **Cue actions** are ordinary actions, run at the cue's time: `at` for a point cue, an overlay's
  `to` for its buttons.
  - Starting playback (`play`, `playAll`, `resume`) **interrupts** the current one. When the new
    playback ends or the viewer stops it, the interrupted one continues from the cue's time. `after`
    is not allowed there.
  - `goto` leaves playback; it is remembered for `resume` at the cue's time.
  - `back` stops playback, like the remote's Back.
  - `set`, `if` and `do` work as anywhere; `activate` is not allowed.
- **Players** watch for the cues listed in the play effect and report `cue` (a point cue's `at`,
  an overlay's `from`) and `cueEnd` (an overlay's `to`, or leaving its window by seeking). A player
  that can't draw over its video SHOULD still run point cues; without Level 2, cues are ignored and
  `end` plays to the end of the media.

## 7. Cross-reference rules

Validators MUST reject a manifest when:
- a `media`, `menu`, `rootMenu`, `defaultButton`, `goto.button`, `activate.button` or `nav` target doesn't exist, or
- `activate` is used anywhere but a menu's `back` or `timeout`, or
- a `resume`'s `otherwise` is another `resume`, or
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
- Vendor or tool data goes in properties starting with `x-`, e.g. `x-dvd` for DVD provenance (see [profiles/dvd.md](profiles/dvd.md)). Validators SHOULD warn about unknown properties that don't start with `x-`, because they're usually typos.
- A manifest with a **newer minor version** MUST still be read: its unknown fields are ignored.
- A manifest with a **different major version** MUST be rejected rather than guessed at.

## 10. Conformance levels

| Level | A player must support |
|---|---|
| **0: Basic** | The fallback list and playback. |
| **1: Menus** | Everything in this document: canvases and aspect, backgrounds, buttons, state overlays, nav/autoNav, autoAction, chapters, all actions, and the navigation semantics. |
| **2: Enhanced** | *(partial)* Motion backgrounds, menu audio, `buttonsAt`, transitions and skipping, menu timeouts, variables, player state, conditions and logic (§6.1), audio/subtitle selection, angles (§4.1), cues and buttons during playback and stopping at `end` (§6.3). Menus per language are separate menus chosen with `@menuLanguage`. *(Next draft)* Segment playlists for seamless branching (see [DVD profile: Not supported yet](profiles/dvd.md#not-supported-yet)). |

Players claim a level by passing that level's cases in [`conformance/`](../conformance). Each case lists input events and the expected state and effects, so any implementation in any language can run them.
