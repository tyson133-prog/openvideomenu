# @openvideomenu/renderer-web

A web player for [Open Video Menu](https://openvideomenu.org) menus, with a 10-foot UI for TVs: backgrounds, buttons and highlights, driven by a remote or a keyboard. It plays all four levels, and uses [`@openvideomenu/core`](https://www.npmjs.com/package/@openvideomenu/core) for validation and navigation.

It plugs into any host through three adapters:

- **Playback:** plays the film and extras. The default uses an HTML5 `<video>` element over the menu.
- **Input:** turns keys into up, down, left, right, enter and back. The default listens for the keyboard and TV-remote keys.
- **Media resolver:** turns a media ref into something playable. Paths resolve against the manifest; `{ scheme, id }` refs go to your resolvers.

## Install

```sh
npm install @openvideomenu/renderer-web
```

## Show a menu

```js
import { loadPlayer } from "@openvideomenu/renderer-web";

// Fetches, validates and starts the menu. Problems with the file are shown on screen instead.
const player = await loadPlayer(document.getElementById("player"), "/films/Sintel (2010)/Sintel (2010).openvideomenu.json", {
  onExit: () => history.back(), // Back pressed at the top menu
});
```

For a bundle, give the manifest inside it: `…/Sintel (2010).ovmz/manifest.json`. Servers that support the format serve bundle members at that path. Media paths in a bundle's manifest resolve beside the bundle.

If you already have a parsed manifest, use the class:

```js
import { MenuPlayer } from "@openvideomenu/renderer-web";

const player = new MenuPlayer(container, { manifest, manifestUrl });
player.start();
// later: player.destroy();
```

## Options

| Option | What it does |
|---|---|
| `playback` | `(container) => PlaybackAdapter`: your own playback (a native player, your server's player). Default: `Html5Playback`. |
| `input` | An `InputAdapter`. Default: `KeyboardInput` (arrows, Enter, Space, Escape, Backspace, the TV Back key; M and T for the Menu and Title keys). |
| `resolvers` | `{ [scheme]: (ref) => url }` for media refs like `{ "scheme": "jellyfin", "id": "…" }`. |
| `level` | The highest level your host supports (0–3, default 3). A manifest that needs more shows its plain list. |
| `view` | `"fallback"` shows the plain list even when there are menus. |
| `initialLastStop` | `{ media, position }`: where the viewer last stopped, for "resume" buttons. |
| `player` | What you know of the viewer's setup as `@` player state, e.g. `{ "@audioLanguage": "fr" }`. Default: the browser's language. |
| `answerParental`, `parentalLevel` | Level 3: answer a menu's request for a higher parental level, or the highest level allowed without asking (default 8). |
| `onExit`, `onStateChange`, `onOverlay` | Back at the top menu; every navigator state; an overlay's buttons showing during playback. |
| `showHidden` | Show hidden groups in the plain list. |

## Your own playback

Implement `PlaybackAdapter`: `play(request)`, `stop()` (returns the position reached) and `destroy()`. Report back through `onEnded`, `onStopped(position)` and `onError(message)`.

`request` has the media id, the ref as written, the resolved `url` when there is one, the `start` time and a `label`. With Level 2 manifests it may also carry audio and subtitle tracks, an angle, an `end` time, cues to report (`onCue`, `onCueEnd`), frames to hold, and `segments` for a title made of several file ranges. The optional `currentPosition()`, `seek(position)`, `switchAudio(audio)` and `endHold()` let the menus do more when your player can.

## Inside a native app

`dist/host.html` with `dist/openvideomenu-player.js` runs the renderer in a web view. The native app injects a message port named `openVideoMenuHost` and plays the video itself. The page asks for playback with `play` messages; the app reports `ended`, `stopped` and remote keys back. The message list is at the top of `src/host.ts`. `runInHost(port, container)` starts it.

`dist/openvideomenu-player.js` is also a standalone script for any page: it defines the global `OpenVideoMenuPlayer`.

## Browsers

- The default playback plays what the browser can: usually H.264/AAC MP4 and WebM, not every MKV.
- Browsers block sound until the viewer presses something, so motion menus start muted and get their sound with the first key or click.

## More

- [The specification](https://openvideomenu.org/spec/)
- `openvideomenu preview <folder>` in [`@openvideomenu/cli`](https://www.npmjs.com/package/@openvideomenu/cli) shows a folder's menus with this player.

Apache-2.0.
