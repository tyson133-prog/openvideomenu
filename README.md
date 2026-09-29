# Open Video Menu

**An open, royalty-free format that brings menus to the films and shows you already have.**

> v0.1 **draft**. Names and fields may change before v1.0.

## Why

When movies went digital, bonus features mostly disappeared. It wasn't that people stopped caring. The **menu** was gone, and the menu was what tied the extras to the film. Without it, extras became loose files next to the movie (easy to forget) or were dropped entirely.

Open Video Menu brings the menu back as a small JSON file that sits next to your movie:

- a **full menu**: background art, buttons with highlight states, remote-control navigation, submenus, scene selection
- where every button just **points to** files you already have: the film, trailers, featurettes, episodes
- in a format **any player can support for free**, with a plain list fallback so even the simplest player works

```
Sintel (2010)/
  Sintel (2010).mp4
  Sintel (2010).openvideomenu.json   ← the menu
  menus/                          ← its art
  Trailers/Sintel Trailer.mp4
  Other/Music and Effects Track.mp4
```

Folder names follow the Plex/Jellyfin conventions people already use, so there's nothing to reorganize.

## Try it

You'll need Node.js 22.2+ and ffmpeg.

```sh
npm install
npm run build
node examples/sintel/build.mjs         # downloads the trailer and score (~80 MB), renders the menus
npm run preview:sintel                 # open http://127.0.0.1:8765
```

- Use the arrow keys, Enter, and Esc/Backspace, as on a remote. The mouse works too.
- `node examples/sintel/build.mjs --film` also downloads the full film (1.2 GB) instead of using a stand-in.
- `node examples/dvd-shaped/build.mjs && npm run preview:dvd` shows a synthetic **DVD-shaped** title: a 720×480 anamorphic menu, flat DVD highlights, auto-action page arrows and a hidden easter egg.
- To test on a TV, run `node packages/cli/dist/main.js preview <folder> --host 0.0.0.0` and open the address in the TV's browser.

Validate your own manifests with:

```sh
node packages/cli/dist/main.js validate <file-or-folder>
```

## What's here

| Path | What it is |
|---|---|
| [`spec/openvideomenu-v0.1.md`](spec/openvideomenu-v0.1.md) | The format (draft) |
| [`spec/schema/manifest.schema.json`](spec/schema/manifest.schema.json) | JSON Schema |
| [`spec/server-api.md`](spec/server-api.md) | The API servers use to hand manifests and menu art to apps (draft) |
| [`spec/COMPATIBILITY.md`](spec/COMPATIBILITY.md) | The backward-compatibility charter |
| [`spec/profiles/dvd.md`](spec/profiles/dvd.md) | How importers map DVD menus into the format |
| [`conformance/`](conformance) | Language-neutral test cases: manifests, input events, expected results |
| [`packages/core`](packages/core) | Validator and navigation state machine (TypeScript, no dependencies) |
| [`packages/renderer-web`](packages/renderer-web) | Reference web player: 10-foot UI, remote keys, pluggable playback |
| [`packages/cli`](packages/cli) | `openvideomenu validate` and `openvideomenu preview` |
| [`examples/`](examples) | Sintel (Blender open movie) and a synthetic DVD-shaped title |

## For player developers

A player plugs in three small pieces:
- **playback:** play a URL from a time, report when it ends or stops
- **input:** turn remote or keyboard presses into up/down/left/right/enter/back
- **media resolver:** turn a media reference into a playable URL

Navigation logic is a pure function (`step(manifest, state, event)`) that you can reuse or re-implement. Either way, the [conformance cases](conformance/cases) tell you exactly how it must behave.

A **Level 0** player only needs the fallback list, which takes about an afternoon of work.

## Compatibility promise

From v1.0: old manifests work forever, fields are never removed or redefined, unknown fields are ignored, and every manifest carries a plain list fallback. See the [charter](spec/COMPATIBILITY.md).

## Roadmap

- **Level 2:** motion menus, menu music, timeouts, variables, and audio/subtitle setup menus
- **Jellyfin plugin:** menus inside Jellyfin's web, TV and mobile apps
- **.NET library:** so C# tools, such as importers and the Jellyfin plugin, can read and write manifests
- **Tools:** `openvideomenu init` to build a menu from an existing Plex/Jellyfin folder, plus a visual menu editor
- **v1.0:** a final name, a frozen format, the compatibility charter in force

Plex removed third-party plugins, so a Plex integration is up to Plex. The format is designed so adding it is easy.

## License and governance

**Open Video Menu** ([openvideomenu.org](https://openvideomenu.org)) was created and is edited by Tyson Treasure.

- **Code:** [Apache-2.0](LICENSE)
- **Specification:** [CC BY 4.0](spec/LICENSE.md), with a royalty-free patent pledge (OWFa 1.0). Free to implement.
- **Governance:** how the format changes and who decides, in [GOVERNANCE.md](GOVERNANCE.md).
- **The name:** using "Open Video Menu" and claiming compatibility, in [NAMING.md](NAMING.md).
- **Contributing:** sign-off (DCO) and licenses of contributions, in [CONTRIBUTING.md](CONTRIBUTING.md).
- **Authors:** [AUTHORS.md](AUTHORS.md).
- **Sintel:** © Blender Foundation | durian.blender.org, used under CC BY 3.0. It's downloaded by the example script and not stored in this repository.
