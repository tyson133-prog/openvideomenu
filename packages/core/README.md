# @openvideomenu/core

The reference implementation of [Open Video Menu](https://openvideomenu.org) 0.1: a validator and a navigator for menu manifests. No dependencies; it runs in browsers and Node.

- **The validator** checks a manifest against the specification and reports errors and warnings by JSON path.
- **The navigator** is the menus' behaviour as a pure state machine: `step(manifest, state, event)` gives the new state and the effects for your player to carry out. It doesn't play, draw or listen for keys; your player does that.

The conformance cases are written in the same terms (events in, state and effects out), so a player built on this package behaves as the specification says.

## Install

```sh
npm install @openvideomenu/core
```

## Example

```js
import { parseManifest, initialState, step, levelUsed } from "@openvideomenu/core";

const result = parseManifest(await (await fetch(manifestUrl)).text());
if (!result.ok) throw new Error(result.errors.map((e) => `${e.path}: ${e.message}`).join("\n"));
const manifest = result.manifest;
console.log(`This manifest uses Level ${levelUsed(manifest)}`);

let state = initialState();
function send(event) {
  const { state: next, effects } = step(manifest, state, event);
  state = next;
  for (const effect of effects) {
    if (effect.type === "play") player.play(manifest.media[effect.media], effect.start); // your player
    if (effect.type === "exit") player.close();
  }
  draw(state); // state.mode, state.menu, state.focus
}

send({ type: "start" });                  // first play, or the root menu
send({ type: "press", key: "down" });     // remote keys: up, down, left, right, enter, back
send({ type: "press", key: "enter" });
// When playback ends: send({ type: "ended" }); when the viewer stops it: send({ type: "stopped", position })
```

## What it exports

| Export | What it does |
|---|---|
| `parseManifest(text)` | Parse JSON text and validate it. Returns `{ ok, errors, warnings, manifest }`. |
| `validateManifest(data)` | The same, for an object you have already parsed. |
| `initialState()` | A navigator state before `start`. |
| `step(manifest, state, event)` | Apply one event (a key, the end of playback, a cue…). Returns `{ state, effects }`. |
| `sessionOf(state)` | Level 3: what to keep between viewings (variables, player state, resume point), as plain JSON. Give it back with `{ type: "start", session, resume? }` (spec §6.5.1). |
| `levelUsed(manifest)` | The conformance level (0–3) the manifest's menus use. |
| `resolveMedia(ref, manifestUrl, resolvers?)` | Turn a media ref into a URL. Paths in a bundle's manifest resolve beside the bundle. Refs like `{ scheme, id }` go to your resolvers. |
| `resolvePath(path, manifestUrl)` | Resolve a menu asset path against the manifest. |
| `menuAssets(manifest)` | Every menu asset the manifest names (backgrounds, highlights, music, transitions, thumbnails). Never the film or extras. Servers use it as an allowlist. |
| `buttonsShown`, `overlayOf`, `chapterAt`, `startTime`, `nearestButton`, `angleCount`, `isClip`, `canvasAspect`, `parseAspect` | Helpers for drawing menus and playing titles. |
| `FORMAT_ID`, `FORMAT_VERSION`, `MANIFEST_SUFFIX`, `BUNDLE_EXTENSION`, `SCHEMA_ID`, `isManifestFileName`, `isBundleFileName` | The format's names and file suffixes. |

TypeScript types come with it: `Manifest`, `NavState`, `NavEvent`, `Effect`, `Issue` and the rest.

## Levels

A player supports a level, and a manifest says which level its menus need (`level`). Below that, players show the manifest's plain list instead.

| Level | What the player supports |
|---|---|
| 0, Basic | The fallback list and playback |
| 1, Menus | Menus: backgrounds, buttons, highlights, navigation, chapters |
| 2, Enhanced | Motion menus and music, transitions, timeouts, angles, segments, cues and buttons during playback, remote menu keys |
| 3, Disc logic | Program state: variables, conditions, named actions, the resume point, timers, parental requests |

Effects and events marked Level 2 or 3 in the types only appear in manifests that use those levels.

## More

- [The specification](https://openvideomenu.org/spec/)
- The conformance cases are in `conformance/cases` in the project's repository. They are plain JSON: a manifest, then events with the state and effects that must follow. Run them in any language.
- [`@openvideomenu/renderer-web`](https://www.npmjs.com/package/@openvideomenu/renderer-web) is a web player built on this package.

Apache-2.0. The specification is CC BY 4.0.
