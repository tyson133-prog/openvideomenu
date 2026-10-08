# Open Video Menu asset server (proof of concept)

A small, **read-only** server that runs next to your Plex server (or Jellyfin, without the plugin) and hands menu manifests and menu art to the ovmplayer TV app over your home network.

> **This is a stopgap, not the product.** It exists only because Plex and Jellyfin can't serve menu files themselves yet. If media servers support the format natively, this goes away.

## What it does and doesn't do

- **Follows your libraries.** With a Plex token or a Jellyfin API key, it asks your media server for its library folders (movie and TV libraries) and keeps following them: a library you add is picked up within 5 minutes. Without one, it scans the folders you give it.
- Scans those folders for `*.openvideomenu.json` manifests and `.ovmz` bundles. With a token it also listens to your media server's notifications, so a film's menu shows up as soon as the media server has the film; it then rescans the folders every 5 minutes (for menus added to films already in the library). Without a token, or while the notifications are down, it rescans every 30 seconds.
- **Says which library item each menu belongs to** (with a token): the index names the Plex or Jellyfin item that plays each menu's media, so the TV apps find menus at once instead of searching your library.
- Serves **only** those manifests and the menu art they name: backgrounds, highlights, menu music, transitions, chapter thumbnails.
- **Never serves your media.** Films, episodes and extras are refused. Any file no manifest names as menu art can't be reached at all.
- **Never writes anything.** Mount your media read-only.
- **Home network only.** It answers only local and VPN addresses: 10.x, 172.16–31.x, 192.168.x, and 100.64.x (Tailscale).
- **Checks each viewer's Plex or Jellyfin sign-in.** The TV app sends its sign-in. For Plex, the server asks *your* Plex server whether it accepts that token, so menus keep working when the internet is down. (If your Plex server lets your network in without signing in, it would accept any token, so then the server asks plex.tv whether the token can access your Plex server.) For Jellyfin, it asks your Jellyfin server whether it accepts the token (`GET /Users/Me`). Answers are cached briefly in memory; viewers' tokens are never stored.
- **Keeps one credential of its own, if you give it one:** the Plex token or Jellyfin API key it reads your library folders with. It sits in the server's settings, so keep that file readable only by the server and administrators (the installers do this for you). The server only reads with it (the libraries, their items, and change notifications); it never changes anything on your media server, and never logs it.
- **Announces itself** on the network (`_openvideomenu._tcp`), so the TV app finds it without typing an address. It also answers legacy unicast mDNS queries (RFC 6762 §6.7), which is how the Roku app browses.

## The token

- **Plex:** a token of your Plex account (the server owner's, so it sees every library). In Plex Web, open any item, choose **Get Info → View XML**, and copy the `X-Plex-Token` value from the address bar. On a Windows PC that runs Plex, it's also in the registry: `HKEY_CURRENT_USER\Software\Plex, Inc.\Plex Media Server`, value `PlexOnlineToken`.
- **Jellyfin:** in the Dashboard, open **API Keys**, choose **+**, and name it "Open Video Menu".

The library paths come as your media server sees them. When the menu server sees them somewhere else (Plex runs in Docker with your films at `/data`, and the menu server has them at `/mnt/media`), translate them with `--path-map /data=/mnt/media`.

## Run with Docker (Linux, Unraid, Synology)

```sh
docker run -d --name openvideomenu --restart unless-stopped --network host \
  -v /path/to/media:/data:ro \
  -e PLEX_URL=http://127.0.0.1:32400 -e PLEX_TOKEN=your-token \
  ghcr.io/tyson133-prog/openvideomenu-menu-server:latest
```

Mount your media at the same path Plex uses (here `/data`, as in most Plex containers), or add `-e PATH_MAP=/plex-path=/container-path`. `--network host` lets the TV app discover the server automatically. Without it, publish the port (`-p 8790:8790`) and enter the address in the app by hand.

Docker Compose: [`docker/compose.yaml`](../../docker/compose.yaml) (edit the media folders, server address and token,
then `docker compose -f docker/compose.yaml up -d`). To build the image yourself instead, from a checkout:
`docker build -f docker/asset-server.Dockerfile -t openvideomenu-menu-server .`

## Run as a Linux service (systemd)

See [`packaging/linux`](../../packaging/linux): a locked-down unit that starts at boot, runs as its own user
with read-only access to your media, and restarts on failure.

## Run with Node (e.g. Plex on Windows)

```sh
npm install && npm run build
node packages/asset-server/dist/main.js --plex-url http://127.0.0.1:32400 --plex-token your-token
```

Or without a token, with the folders: `--media "D:\Movies" --media "D:\TV" --plex-url http://127.0.0.1:32400`.
Settings can also live in a file (`--config menu-server.env`, with lines like `PLEX_TOKEN=…`).

## Options

| Flag | Environment | Default | |
|---|---|---|---|
| `--plex-url <url>` | `PLEX_URL` | — | Your Plex server; Plex sign-ins are checked against it |
| `--plex-token <token>` | `PLEX_TOKEN` | — | Read Plex's library folders with this token, and follow them |
| `--jellyfin-url <url>` | `JELLYFIN_URL` | — | Your Jellyfin server; Jellyfin sign-ins are checked against it. Give either or both. |
| `--jellyfin-api-key <key>` | `JELLYFIN_API_KEY` | — | Read Jellyfin's library folders with this API key, and follow them |
| `--media <folder>` (repeatable) | `MEDIA_ROOTS` (comma-separated) | — | Folders to scan besides the libraries' (or instead of them, without a token) |
| `--path-map <from>=<to>` (repeatable) | `PATH_MAP` (`;`-separated) | — | A library path as this machine sees it, e.g. `/data=/mnt/media` |
| `--config <file>` | `OVM_CONFIG` | — | Read settings from a `KEY=VALUE` file; the environment and flags win over it |
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
