# Conformance suite

A player is **compatible** with Open Video Menu 0.1 at a level when it passes every case for that level
and the levels below it. The levels are in [spec §10](../spec/openvideomenu-v0.1.md#10-conformance-levels):
0 Basic, 1 Menus, 2 Enhanced, 3 Disc logic.

The cases are plain JSON files in [`cases/`](cases), 69 of them. Any language can run them. The reference
TypeScript implementation, the .NET library and the Roku player all run the same files.

## A case

Each file is one manifest and what must happen with it. The smallest one,
[`00-level0-minimal.json`](cases/00-level0-minimal.json):

```json
{
  "description": "Level 0: a manifest with no menus is still valid; players show the fallback list.",
  "level": 0,
  "valid": true,
  "manifest": {
    "format": "openvideomenu",
    "version": "0.1",
    "title": { "name": "Minimal" },
    "media": { "feature": { "ref": "Minimal.mp4", "kind": "feature" } },
    "fallback": [
      { "label": "Movie", "items": [{ "label": "Play", "action": { "type": "play", "media": "feature" } }] }
    ]
  },
  "steps": [{ "event": { "type": "start" }, "expect": { "mode": "fallback" } }]
}
```

| Field | Meaning |
|---|---|
| `description` | What the case checks. |
| `level` | The level the manifest uses (0–3). Run a case if your player supports this level. |
| `manifest` | The manifest under test. |
| `valid` | Whether a validator must accept it. |
| `schemaValid` | Whether the [JSON Schema](../spec/schema/manifest.schema.json) must accept it. Defaults to `valid`. A manifest can pass the schema but break a cross-reference rule only a validator checks ([`51-invalid-references.json`](cases/51-invalid-references.json)). |
| `errors` | Paths a validator must report as errors, e.g. `/menus/main/buttons/2/nav/up`. Your validator may report more. |
| `warnings` | Paths a validator must report as warnings. Your validator may report more. |
| `steps` | Events to run through the navigator, in order, each with what must follow. |

Each step is `{ "event": …, "expect": { … } }`. Every field of `expect` is optional; check only those given:

| `expect` field | The state after the event must have |
|---|---|
| `mode` | `"idle"`, `"menu"`, `"playing"`, `"fallback"` or `"transition"` |
| `menu` | this menu id |
| `focus` | this button id |
| `intro` | `true` when the menu was entered fresh (its intro plays), `false` when returned to |
| `buttonsReady` | `false` while a menu intro runs before its buttons appear |
| `overlay` | `{ "cue", "focus" }` of the buttons showing during playback, or `null` for none (Level 2) |
| `effects` | exactly these effects, **in this order** |

Cases whose `valid` is `false` have no steps. Cases 50 and up are mostly invalid manifests: they test
the validator.

## Writing a harness

Your navigator needs two things: a starting state, and a function that takes the manifest, the state and
one event, and returns the new state and a list of effects. That is how the reference implementation
works (`initialState()` and `step(manifest, state, event)` in [`@openvideomenu/core`](../packages/core)).
Your player can be built differently inside, as long as the harness can drive it this way.

```text
for each file in conformance/cases/*.json:
    case = parse(file)
    skip if case.level > your level

    result = validate(case.manifest)
    assert result.ok == case.valid
    assert every path in case.errors   is among result's error paths
    assert every path in case.warnings is among result's warning paths

    if case.steps:
        state = initialState()
        for each { event, expect } in case.steps:
            (state, effects) = step(case.manifest, state, event)
            for each field in expect:
                if field == "effects": assert effects == expect.effects     # same items, same order
                else:                  assert state[field] == expect[field]  # overlay: null = none
```

Compare effects as JSON values: same fields, same values. Leave out fields that are absent rather than
writing them as `null`.

Also check that you accept a manifest with a newer minor version and unknown fields
([`53-newer-minor-and-unknown-fields.json`](cases/53-newer-minor-and-unknown-fields.json): a warning, not
an error), and reject a newer major version
([`54-invalid-major-version.json`](cases/54-invalid-major-version.json)). That is what keeps old players
working with new manifests ([`COMPATIBILITY.md`](../spec/COMPATIBILITY.md)).

## Events

What the player reports. Any event may also carry `time`, the player's clock in seconds (any origin,
never going back), which Level 3 timers and counters read.

| Event | When |
|---|---|
| `{ "type": "start", "player"?, "seed"? }` | Begin: run `firstPlay` or open the root menu. `player` holds `@` player state (languages, region, …); `seed` seeds random play. |
| `{ "type": "press", "key", "position"? }` | A remote key: `up`, `down`, `left`, `right`, `enter`, `back`, and at Level 2 the menu keys `menu`, `title`, `audioMenu`, `subtitleMenu`, `angleMenu`, `chapterMenu`. During playback, a menu key carries the `position` it was pressed at. |
| `{ "type": "select", "button" }` | A pointer moved onto a button. |
| `{ "type": "activate", "button" }` | A pointer clicked a button. |
| `{ "type": "ended" }` | The playing item finished on its own. |
| `{ "type": "stopped", "position" }` | The viewer stopped playback early, at `position` seconds. |
| `{ "type": "buttonsReady" }` | The menu intro reached `buttonsAt`. |
| `{ "type": "transitionEnded" }` | A transition clip finished. |
| `{ "type": "timeout", "menu" }` | Level 2: a menu's countdown (`startTimer`) ran out. |
| `{ "type": "cue", "id" }` | Level 2: playback passed a cue's time, or entered an overlay's window. |
| `{ "type": "cueEnd", "id" }` | Level 2: playback left an overlay's window. |
| `{ "type": "buttonSet", "index" }` | Level 2: the menu's clock entered a button set's window (`null`: left them all). |
| `{ "type": "player", "values" }` | Level 2: player state changed outside the menus, e.g. the viewer picked another soundtrack. |
| `{ "type": "navTimer", "position"? }` | Level 3: the navigation timer ran out. |
| `{ "type": "parental", "granted" }` | Level 3: the answer to `askParental`. |

## Effects

What the player must do.

| Effect | Do |
|---|---|
| `{ "type": "play", "media", "start", … }` | Play a media item from `start` seconds. Level 2 adds `audio`, `subtitle`, `angle`, `end`, `cues`, `prohibits`, `prohibitsAt` and `holds`. |
| `{ "type": "activated", "menu", "button" }` | Briefly show the button's activated state. During playback it names the overlay's `cue` instead of a `menu`. |
| `{ "type": "transition", "src" }` | Play a transition clip, then report `transitionEnded`. |
| `{ "type": "skipIntro", "menu" }` | The viewer skipped the intro: jump the background to `buttonsAt`. |
| `{ "type": "exit" }` | Back was pressed at the top of the menu history: you may close the player. |
| `{ "type": "startTimer", "menu", "after" }` | Level 2: report `timeout` for that menu after `after` seconds, unless replaced or the viewer leaves it. |
| `{ "type": "seek", "position" }` | Level 2: move the playback in progress to `position`. |
| `{ "type": "startNavTimer", "after" }` | Level 3: report `navTimer` after `after` seconds. |
| `{ "type": "cancelNavTimer" }` | Level 3: stop the navigation timer. |
| `{ "type": "askParental", "level" }` | Level 3: answer with a `parental` event. |
| `{ "type": "tracks", "audio"?, "subtitle"?, "angle"?, "at" }` | Level 3: change tracks of the playback in progress now. |
| `{ "type": "budgetExceeded" }` | Level 3: one event ran too many actions and the rest were dropped. For diagnostics only. |

The exact shapes are in [`packages/core/src/navigator.ts`](../packages/core/src/navigator.ts) (`NavEvent`,
`Effect`, `NavState`), with a comment on each.

## Running the existing harnesses

| Implementation | Command | Notes |
|---|---|---|
| TypeScript (reference) | `npm test` at the repo root | [`conformance.test.ts`](conformance.test.ts) also checks each case against the JSON Schema, and that the manifest uses exactly the case's `level`. |
| .NET | `dotnet test sdk/dotnet/OpenVideoMenu.Tests` | [`ConformanceTests.cs`](../sdk/dotnet/OpenVideoMenu.Tests/ConformanceTests.cs) |
| Roku | `node tools/deploy.mjs --conformance` in `apps/roku` | Sideloads to a Roku in developer mode and prints results from its debug console. It runs the cases with steps (valid manifests only) and skips the rest. |

## Adding a case

Name it `NN-what-it-checks.json` (numbers 50 and up for invalid manifests), give it the lowest `level` that
its manifest uses, and run every harness. A change to the spec that changes behaviour comes with a case.
