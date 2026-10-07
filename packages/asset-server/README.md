# Open Video Menu asset server (proof of concept)

A small, **read-only** server that runs next to your Plex server and hands menu manifests and menu art to the ovmplayer TV app over your home network.

> **This is a stopgap, not the product.** It exists only because Plex and Jellyfin can't serve menu files themselves yet. If media servers support the format natively, this goes away.

## What it does and doesn't do

- Scans your media folders for `*.openvideomenu.json` manifests and `.ovmz` bundles, and again every 30 seconds, so new or removed menus show up within half a minute.
- Serves **only** those manifests and the menu art they name: backgrounds, highlights, menu music, transitions, chapter thumbnails.
- **Never serves your media.** Films, episodes and extras are refused. Any file no manifest names as menu art can't be reached at all.
- **Never writes anything.** Mount your media read-only.
- **Home network only.** It answers only local and VPN addresses: 10.x, 172.16–31.x, 192.168.x, and 100.64.x (Tailscale).
- **Checks your Plex or Jellyfin login.** The TV app sends its sign-in. For Plex, the server asks plex.tv whether that token can access *your* Plex server. For Jellyfin, it asks your Jellyfin server whether it accepts the token (`GET /Users/Me`). Answers are cached briefly in memory. It stores no credentials.
- **Announces itself** on the network (`_openvideomenu._tcp`), so the TV app finds it without typing an address. It also answers legacy unicast mDNS queries (RFC 6762 §6.7), which is how the Roku app browses.

## Run with Docker (Linux, Unraid, Synology)

```sh
docker build -f docker/asset-server.Dockerfile -t openvideomenu-asset-server .
docker run -d --name openvideomenu --restart unless-stopped --network host \
  -v /path/to/media:/media:ro \
  -e PLEX_URL=http://127.0.0.1:32400 \
  openvideomenu-asset-server
```

`--network host` lets the TV app discover the server automatically. Without it, publish the port (`-p 8790:8790`) and enter the address in the app by hand.

Docker Compose: [`docker/compose.yaml`](../../docker/compose.yaml) (edit the media folders and server address, then
`docker compose -f docker/compose.yaml up -d --build`).

## Run as a Linux service (systemd)

See [`packaging/linux`](../../packaging/linux): a locked-down unit that starts at boot, runs as its own user
with read-only access to your media, and restarts on failure.

## Run with Node (e.g. Plex on Windows)

```sh
npm install && npm run build
node packages/asset-server/dist/main.js --media "D:\Movies" --media "D:\TV" --plex-url http://127.0.0.1:32400
```

## Options

| Flag | Environment | Default | |
|---|---|---|---|
| `--media <folder>` (repeatable) | `MEDIA_ROOTS` (comma-separated) | — | Media folders to scan |
| `--plex-url <url>` | `PLEX_URL` | — | Your Plex server; Plex sign-ins are checked against it |
| `--jellyfin-url <url>` | `JELLYFIN_URL` | — | Your Jellyfin server; Jellyfin sign-ins are checked against it. Give either or both. |
| `--port <n>` | `PORT` | 8790 | |
| `--name <text>` | `SERVER_NAME` | "Open Video Menu on &lt;host&gt;" | Name shown in the TV app |
| `--no-discovery` | `DISCOVERY=off` | on | Don't announce on the network |
| `--allow-remote` | `ALLOW_REMOTE=1` | off | Answer non-local addresses (not recommended) |
| `--no-auth` | `AUTH=none` | off | Skip token checks. **Local testing only.** |

## API (v1)

| Request | Auth | Returns |
|---|---|---|
| `GET /v1/health` | none | `{ok, api, format, manifests, auth}` |
| `GET /v1/index[?refresh]` | token | Every manifest: path, title, validity, the media paths it references (relative to the media folder), and its menu assets |
| `GET /v1/files/<root>/<path>` | token | A manifest or one of its menu assets (supports Range requests) |

The sign-in goes in a header, never in the URL: a Plex token in `X-Plex-Token` (or `Authorization: Bearer`), or a Jellyfin token in Jellyfin's own `Authorization: MediaBrowser ..., Token="..."` header.
