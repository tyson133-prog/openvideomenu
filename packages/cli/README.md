# @openvideomenu/cli

The [Open Video Menu](https://openvideomenu.org) command line, for people who make menus: check manifests and bundles, preview menus in a browser, and pack a menu and its art into one `.ovmz` file.

## Install

Needs Node.js 22.2 or later.

```sh
npm install -g @openvideomenu/cli
```

It installs two names for the same command: `openvideomenu` and `ovm`.

## Commands

### validate

```sh
openvideomenu validate <file-or-folder>
```

Checks a manifest (`*.openvideomenu.json`), a bundle (`*.ovmz`), or every one in a folder and below, with the same rules players use. For each file it prints errors and warnings by JSON path, and the level the menus use. It also warns when menu art is missing, when an MP4 or M4A has its index at the end of the file (some TV players stall on those: run `ffmpeg -movflags +faststart`), and when a folder holding menu video or music has no `.plexignore` or `.ignore` (Plex or Jellyfin would list the clips as titles).

### preview

```sh
openvideomenu preview <folder> [--port 8765] [--host 127.0.0.1] [--open]
```

Serves the folder and plays its menus in a browser, with the arrow keys, Enter and Esc or Backspace as a remote. Use `--host 0.0.0.0` to open it from a TV's browser on your network, and `--open` to open your browser. It runs until you press Ctrl+C.

### pack

```sh
openvideomenu pack <manifest-or-folder> [--out <bundle.ovmz>]
```

Packs each manifest and the menu art it names into `<Title>.ovmz` beside it. A bundle is a zip with every entry stored uncompressed and the manifest at `manifest.json`, so servers can stream its members. `--out` sets the bundle's path, for a single manifest.

### unpack

```sh
openvideomenu unpack <bundle.ovmz> [--out <folder>]
```

Unpacks a bundle beside it, or into `--out`, as `<Title>.openvideomenu.json` and its art.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Done (`validate`: every file is valid) |
| 1 | `validate`: a file has errors, or there are no manifests; `pack`: a manifest couldn't be packed; an unknown command; any other error |

## Example

```sh
openvideomenu validate "Movies/Sintel (2010)"
openvideomenu preview  "Movies/Sintel (2010)" --open
openvideomenu pack     "Movies/Sintel (2010)/Sintel (2010).openvideomenu.json"
openvideomenu validate "Movies/Sintel (2010)/Sintel (2010).ovmz"
```

## More

- [The specification](https://openvideomenu.org/spec/)
- The library behind it: [`@openvideomenu/core`](https://www.npmjs.com/package/@openvideomenu/core)

Apache-2.0.
