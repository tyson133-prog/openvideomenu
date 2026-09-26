# MenuStream asset server (proof of concept)

A small, **read-only** server that runs next to your Plex server and hands menu manifests and menu art to the MenuStream TV app over your home network.

> **This is a stopgap, not the product.** It exists only because Plex and Jellyfin can't serve menu files themselves yet. If media servers support the format natively, this goes away.

## What it does and doesn't do

- Scans your media folders for `*.menustream.json` manifests.
- Serves **only** those manifests and the menu art they name: backgrounds, highlights, menu music, transitions, chapter thumbnails.
- **Never serves your media.** Films, episodes and extras are refused. Any file no manifest names as menu art can't be reached at all.
- **Never writes anything.** Mount your media read-only.
- **Home network only.** It answers only local and VPN addresses: 10.x, 172.16–31.x, 192.168.x, and 100.64.x (Tailscale).
- **Checks your Plex login.** The TV app sends its Plex token. The server asks plex.tv whether that token can access *your* Plex server, and caches the answer briefly in memory. It stores no credentials.
- **Announces itself** on the network (`_menustream._tcp`), so the TV app finds it without typing an address.

## Run with Docker (Linux, Unraid, Synology)

```sh
docker build -f docker/asset-server.Dockerfile -t menustream-asset-server .
docker run -d --name menustream --restart unless-stopped --network host \
  -v /path/to/media:/media:ro \
  -e PLEX_URL=http://127.0.0.1:32400 \
  menustream-asset-server
```

`--network host` lets the TV app discover the server automatically. Without it, publish the port (`-p 8790:8790`) and enter the address in the app by hand.

Docker Compose:

```yaml
services:
  menustream:
    build: { context: ., dockerfile: docker/asset-server.Dockerfile }
    network_mode: host
    restart: unless-stopped
    environment:
      PLEX_URL: http://127.0.0.1:32400
      MEDIA_ROOTS: /media/movies,/media/tv
    volumes:
      - /mnt/movies:/media/movies:ro
      - /mnt/tv:/media/tv:ro
```

## Run with Node (e.g. Plex on Windows)

```sh
npm install && npm run build
node packages/asset-server/dist/main.js --media "D:\Movies" --media "D:\TV" --plex-url http://127.0.0.1:32400
```

## Options

| Flag | Environment | Default | |
|---|---|---|---|
| `--media <folder>` (repeatable) | `MEDIA_ROOTS` (comma-separated) | — | Media folders to scan |
| `--plex-url <url>` | `PLEX_URL` | — | Your Plex server; tokens are checked against it |
| `--port <n>` | `PORT` | 8790 | |
| `--name <text>` | `SERVER_NAME` | "MenuStream on &lt;host&gt;" | Name shown in the TV app |
| `--no-discovery` | `DISCOVERY=off` | on | Don't announce on the network |
| `--allow-remote` | `ALLOW_REMOTE=1` | off | Answer non-local addresses (not recommended) |
| `--no-auth` | `AUTH=none` | off | Skip token checks. **Local testing only.** |

## API (v1)

| Request | Auth | Returns |
|---|---|---|
| `GET /v1/health` | none | `{ok, api, format, manifests, auth}` |
| `GET /v1/index[?refresh]` | token | Every manifest: path, title, validity, the media paths it references (relative to the media folder), and its menu assets |
| `GET /v1/files/<root>/<path>` | token | A manifest or one of its menu assets (supports Range requests) |

The token goes in the `X-Plex-Token` header (or `Authorization: Bearer`), never in the URL.
