# Open Video Menu for Jellyfin (plugin, proof of concept)

Serves Open Video Menu disc menus straight from your Jellyfin server, so TV apps need **no separate
asset server**. It is the reference for "a media server that supports Open Video Menu natively": the same
`/v1` API as [`packages/asset-server`](../../packages/asset-server), answered by the server that
already holds your library and your sign-in.

## What it does

- **Finds manifests** (`*.openvideomenu.json`) in the folders of your Jellyfin libraries. It rescans
  after every Jellyfin library scan, and in the background every 5 minutes.
- **Serves only what menus need:** the manifests and the menu art they name (backgrounds, highlight
  images, menu music, transitions). It never serves your video. Jellyfin already does that.
- **Uses Jellyfin's own sign-in.** Any signed-in user of this server may read its menus, just as
  they may play its media. There are no extra accounts or tokens.

| Endpoint | Sign-in | What |
|---|---|---|
| `GET /OpenVideoMenu/v1/health` | none | Tells apps this server serves menus: `{ok, api, format, manifests, auth}` |
| `GET /OpenVideoMenu/v1/index[?refresh]` | Jellyfin user | Every manifest, with the media paths it references. Carries an ETag, so an unchanged index is a 304 |
| `GET /OpenVideoMenu/v1/files/<root>/<path>` | Jellyfin user | A manifest or one of its menu assets (supports Range requests) |

ovmplayer checks `/OpenVideoMenu/v1/health` first. When the plugin answers, it uses
Jellyfin for menus and never looks for an asset server.

## Requirements

Jellyfin **12.x** (built against 12.1, .NET 10).

## Install (manual, for now)

1. Build: `dotnet build -c Release` in `Jellyfin.Plugin.OpenVideoMenu/`.
2. Create a folder `OpenVideoMenu_0.1.0.0` in Jellyfin's plugins folder: `/config/plugins/` in the
   official Docker image, or `%ProgramData%\Jellyfin\Server\plugins\` on Windows.
3. Copy `bin/Release/net10.0/Jellyfin.Plugin.OpenVideoMenu.dll`, `bin/Release/net10.0/OpenVideoMenu.dll`
   and `meta.json` into it.
4. Restart Jellyfin. **Dashboard → Plugins** lists "Open Video Menu".

A plugin repository (Dashboard → Plugins → Repositories) comes with the launch.

## Put menus next to your media

As with the asset server: `The Matrix (1999)/The Matrix (1999).openvideomenu.json`, with its art in a
`menus/` folder. That folder should contain an empty `.ignore` file, or Jellyfin lists the menu
clips as titles. Your media folders stay read-only; the plugin only reads.

## Tests

The plugin reads manifests and bundles with the .NET library in `sdk/dotnet`, whose tests
(`dotnet test sdk/dotnet`) run the same conformance cases as the reference implementation
(`packages/core`).
