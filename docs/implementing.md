# Adding Open Video Menu to a player

A starter. The full rules are in the [specification](../spec/openvideomenu-v0.1.md); this page gets you to
a working player and points you there for the rest.

## What a player reads

A **manifest** is one small JSON file next to a film or show: `Sintel (2010)/Sintel (2010).openvideomenu.json`.
It lists the video it plays (`media`), its menus (`menus`), and a plain list for players without menus
(`fallback`). Often it comes as a **bundle**, `Sintel (2010).ovmz`: a ZIP holding `manifest.json` and the menu
art, stored uncompressed ([§2.1](../spec/openvideomenu-v0.1.md#21-bundles)).

The manifest never contains the film. `media` points at files beside it (paths resolve against the folder
the manifest or bundle is in), or at items on a media server (`{ "scheme": "...", "id": "..." }`)
([§4](../spec/openvideomenu-v0.1.md#4-media)).

## Level 0: the plain list (an afternoon)

Every manifest has a `fallback` list ([§8](../spec/openvideomenu-v0.1.md#8-fallback-list-level-0)):

```json
"fallback": [
  { "label": "Movie", "items": [{ "label": "Play Movie", "action": { "type": "play", "media": "feature" } }] },
  { "label": "Special Features", "items": [{ "label": "Trailer", "action": { "type": "play", "media": "trailer" } }] }
]
```

1. Find the manifest: `<Title>.ovmz` or `<Title>.openvideomenu.json` next to the video. Prefer the bundle.
2. Check `format` is `"openvideomenu"` and `version`'s major number is `0`. A newer minor version is fine:
   ignore fields you don't know ([§9](../spec/openvideomenu-v0.1.md#9-unknown-fields-and-extensions)).
3. Show each group's `label` and its items' `label`s. Skip groups with `"hidden": true` unless the viewer asks.
4. When an item is chosen, run its `action`. At this level it is `play` (`media`, and `chapter` (1-based) or
   `start` (seconds)) or `playAll` (`items`, played in order) ([§6](../spec/openvideomenu-v0.1.md#6-actions)).
5. Look the `media` id up in `media`, resolve its `ref`, and play it.

That is a Level 0 player. It already supports every manifest ever written.

## Level 1: menus

At Level 1 you draw the menus and move focus with the remote. You don't have to write the navigation
yourself: it is a pure state machine in the libraries. **Your player supplies three things:**

| You supply | That is |
|---|---|
| **Playback** | Play a URL from a time. Report when it ends on its own, or when the viewer stops it (and where). |
| **Input** | Turn remote or keyboard presses into `up`, `down`, `left`, `right`, `enter` and `back`. |
| **Media resolver** | Turn a media `ref` into something playable: a URL, a file, an item on your server. |

You give the navigator events, and it gives you a new state and a list of effects to carry out. Draw from
the state: the menu in `state.menu`, with focus on `state.focus`.

### TypeScript

```sh
npm install @openvideomenu/core
```

```ts
import { validateManifest, initialState, step, resolveMedia, resolvePath, type NavEvent } from "@openvideomenu/core";

const manifestUrl = "https://server/films/Sintel%20(2010)/Sintel%20(2010).openvideomenu.json";
const result = validateManifest(await (await fetch(manifestUrl)).json());
if (!result.ok) throw new Error(result.errors.map((e) => `${e.path}: ${e.message}`).join("\n"));
const manifest = result.manifest!;

let state = initialState();
send({ type: "start" });

function send(event: NavEvent) {
  const { state: next, effects } = step(manifest, state, event);
  state = next;
  for (const effect of effects) {
    switch (effect.type) {
      case "play": {
        const ref = manifest.media[effect.media]?.ref;
        if (!ref) break;                  // media made of segments (Level 2) have no ref of their own
        const url = resolveMedia(ref, manifestUrl);
        player.play(url, effect.start);   // your playback; call send({ type: "ended" }) or
        break;                            // send({ type: "stopped", position }) when it's over
      }
      case "activated": flash(effect.button); break;   // show the button's activated state briefly
      case "transition": playClip(resolvePath(effect.src, manifestUrl), () => send({ type: "transitionEnded" })); break;
      case "skipIntro": break;                         // Level 2: jump a motion menu to buttonsAt
      case "exit": close(); break;                     // Back at the top of the menus
    }
  }
  if (state.mode === "menu") draw(manifest.menus![state.menu!], state.focus);
}

// Your input: remote.on("key", (key) => send({ type: "press", key }));  key is "up", "down", …, "enter", "back"
```

`draw` paints `background.image` over the menu's `canvas`, scaled to `canvas.aspect`, then each button:
its `states.selected.image` over its `rect` when it has focus, else `states.normal.image`
([§5](../spec/openvideomenu-v0.1.md#5-menus)). With no selected image and no drawn label, draw your own focus
ring. For media on your server, pass resolvers for your schemes: `resolveMedia(ref, manifestUrl, { myserver: (ref) => urlFor(ref.id) })`.

`@openvideomenu/renderer-web` is a complete web player built this way, if you'd rather start from one.

### .NET

`dotnet add package OpenVideoMenu`. `Manifest.Load` reads a manifest or a bundle and validates it;
`Navigator.Step(manifest, state, event)`, starting from `new NavState()`, is the same state machine with the
same results. See the [package README](../sdk/dotnet/OpenVideoMenu/README.md).

### Another language

Write the navigator yourself from the spec ([§5](../spec/openvideomenu-v0.1.md#5-menus) and
[§6](../spec/openvideomenu-v0.1.md#6-actions)), and check it with the conformance suite.

## Getting menus from a media server

Players that play from Plex, Jellyfin or another server get manifests and art through a small read-only
HTTP API ([server API](../spec/server-api.md)):

1. Ask the media server itself: `GET {server}/OpenVideoMenu/v1/health`. If it answers, it serves menus.
2. Otherwise look for a menu server on the network: DNS-SD service type `_openvideomenu._tcp`.
3. `GET {B}/v1/index` lists every manifest with the media paths it refers to; match them to the server's
   items by file path. `GET {B}/v1/files/{root}/{path}` fetches a manifest or one of its assets.
4. Sign in with the media server's own credentials (a Plex token, a Jellyfin token).

If nothing answers, titles must still play, just without menus.

## Level 2 and 3

Each level adds to the one below, and a manifest's `level` says what its menus need. A player below it
shows the fallback list ([§10](../spec/openvideomenu-v0.1.md#10-conformance-levels)).

- **Level 2, Enhanced:** motion menus and menu audio ([§5.1](../spec/openvideomenu-v0.1.md#51-motion-menus)),
  menu timeouts ([§5.2](../spec/openvideomenu-v0.1.md#52-menu-timeouts-level-2)), button sets
  ([§5.3](../spec/openvideomenu-v0.1.md#53-button-sets-level-2)), angles and segments
  ([§4.1](../spec/openvideomenu-v0.1.md#41-angles-level-2), [§4.2](../spec/openvideomenu-v0.1.md#42-segments-level-2)),
  cues and buttons during playback ([§6.3](../spec/openvideomenu-v0.1.md#63-during-playback-level-2)), and the
  remote's menu keys ([§6.6](../spec/openvideomenu-v0.1.md#66-remote-keys-and-prohibitions-level-2)).
- **Level 3, Disc logic:** variables and conditions ([§6.1](../spec/openvideomenu-v0.1.md#61-variables-and-logic-level-3)),
  named actions ([§6.4](../spec/openvideomenu-v0.1.md#64-named-actions-level-3)), the resume point
  ([§6.5](../spec/openvideomenu-v0.1.md#65-the-resume-point-level-3)), timers and counters
  ([§6.7](../spec/openvideomenu-v0.1.md#67-time-the-navigation-timer-and-counters-level-3)) and parental
  requests ([§6.8](../spec/openvideomenu-v0.1.md#68-parental-level-requests-level-3)).

With a library, most of this is more events to send and effects to carry out; the navigator does the logic.

At Level 3, keep the session when the viewer stops: `sessionOf(state)` gives the variables, player state and
resume point as plain JSON. Store it with the title, with the manifest's version, and give it back next time
with `{ type: "start", session }`, or `{ type: "start", session, resume: true }` to carry on from where they
stopped. A disc's settings (a viewing mode, a commentary) then survive the app closing, as they survive a
stop on a DVD player. Drop the session if the manifest changed
([§6.5.1](../spec/openvideomenu-v0.1.md#651-keeping-the-session-between-viewings)).

## Check your player

Run the [conformance suite](../conformance/README.md). When your player passes the cases for a level, it is
compatible at that level. Say your software "supports Open Video Menu" at any time, and "compatible" once
it passes.
