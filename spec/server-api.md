# Open Video Menu server API — v1 (draft)

> **Status: draft.** It follows the manifest format's versioning. After v1.0 the
> [compatibility charter](COMPATIBILITY.md) applies to this API too: `v1` endpoints and fields are
> never removed or redefined.

The key words MUST, MUST NOT, SHOULD, SHOULD NOT and MAY are used as described in RFC 2119.

## 1. Purpose

A TV app needs two things to show a menu: the **manifest** and the **menu art** it names. This
API is how any server hands them to any app. Video is never part of it: the app plays media from the
media server it already uses.

Two kinds of server implement the same API:

| Kind | Example | Base URL `B` |
|---|---|---|
| **Native**: the media server itself | Jellyfin with the Open Video Menu plugin ([`plugins/jellyfin`](../plugins/jellyfin)) | `{media server}/OpenVideoMenu` |
| **Standalone asset server**, for media servers that don't support Open Video Menu yet | [`packages/asset-server`](../packages/asset-server) | `http://{host}:{port}` |

An app written against this API works with both, and keeps working unchanged when a media server
adopts Open Video Menu natively.

## 2. Finding a server (client behaviour)

Apps SHOULD look in this order and use the first that answers:

1. **The media server itself:** `GET {media server}/OpenVideoMenu/v1/health`. Native implementations
   MUST serve the API under that base path, so this one request tells an app whether its server
   supports menus.
2. **An asset server the viewer chose before** (a saved address).
3. **An asset server on the network:** DNS-SD service type `_openvideomenu._tcp`. The instance's
   address and port form `B` (`http://{address}:{port}`). A host may advertise several addresses;
   apps SHOULD use the first whose `/v1/health` answers. If a saved address stops answering, apps
   SHOULD look on the network again before asking the viewer (the host may have a new address).

   Servers that advertise MUST also answer *legacy unicast* queries (RFC 6762 §6.7): a query from a
   port other than 5353 is answered directly to that address and port, repeating the query's ID and
   question. TV platforms often can't bind port 5353 or join the multicast group, so this is how
   their apps browse. Apple's mDNSResponder and Avahi already do this; some mDNS libraries don't.
   An app then takes the server's address from where the answer came from, which also avoids
   addresses the TV can't reach (e.g. a container's own address), and the port from the SRV record.

If none answers, titles MUST still play, just without menus.

## 3. Sign-in

The API uses the viewer's existing media-server sign-in. There are no Open Video Menu accounts.

- **Native servers** use their own authentication, exactly as for their other endpoints (e.g.
  Jellyfin's `Authorization: MediaBrowser …, Token="…"` header).
- **Standalone servers** accept the sign-in of the media servers they are configured for, and check
  it with that media server:

  | Media server | Header the app sends | How the asset server checks it |
  |---|---|---|
  | Plex | `X-Plex-Token: …` (or `Authorization: Bearer …`) | plex.tv lists this Plex server among the token's resources |
  | Jellyfin | `Authorization: MediaBrowser …, Token="…"` | The configured Jellyfin server answers `GET /Users/Me` |

- Credentials MUST travel in headers, never in URLs.
- A request without a valid sign-in MUST get `401`.
- Servers MUST NOT store credentials. They MAY cache verdicts briefly in memory.

## 4. Endpoints

### `GET {B}/v1/health`

No sign-in. Tells apps the server is there and what it speaks.

```json
{ "ok": true, "name": "Open Video Menu for Jellyfin", "api": "1", "format": "0.1", "manifests": 3, "auth": "jellyfin" }
```

| Field | Meaning |
|---|---|
| `api` | API major version, `"1"` here. |
| `format` | Newest manifest format version the server reads. |
| `manifests` | Manifests found so far. It MAY be 0 while a first scan runs. |
| `auth` | What sign-in the server accepts, as a human-readable hint. |

Servers MAY add fields; apps MUST ignore fields they don't know.

### `GET {B}/v1/index`

Signed in. Every manifest the server found, and the media each one references.

```json
{
  "api": "1",
  "format": "0.1",
  "roots": 2,
  "manifests": [
    {
      "id": "3f1c0d9a8b7e6f50",
      "root": 0,
      "path": "The Matrix (1999)/The Matrix (1999).openvideomenu.json",
      "url": "/v1/files/0/The%20Matrix%20(1999)/The%20Matrix%20(1999).openvideomenu.json",
      "valid": true,
      "version": "0.1",
      "title": { "name": "The Matrix", "year": 1999 },
      "source": { "kind": "dvd" },
      "hasMenus": true,
      "media": [
        { "id": "feature", "kind": "feature", "label": "The Matrix", "ref": "The Matrix (1999).mp4", "path": "The Matrix (1999)/The Matrix (1999).mp4" }
      ],
      "modified": "2026-09-26T23:39:12.000Z"
    }
  ]
}
```

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Stable id for this manifest on this server. |
| `root` | yes | Which media folder it is in (index into the server's folders). |
| `path` | yes | The manifest's path under that folder, with forward slashes. |
| `url` | yes | Where to fetch the manifest, relative to `B`. Relative asset paths in the manifest resolve against this URL. |
| `valid` | yes | Whether the manifest could be read. Invalid entries carry `errors` (strings) instead of the fields below. |
| `version`, `title`, `source` | if valid | Copied from the manifest. |
| `hasMenus` | if valid | Whether it has any menus. A manifest with only a fallback list is still valid. |
| `media` | if valid | One entry per media item: `id`, `kind`, `label` and `ref` (when it has one) from the manifest, plus `path`. |
| `media[].path` | when `ref` is a relative path | The media file's path under the same folder, with forward slashes. Apps match it against the media server's file paths by their **trailing segments** (at least the folder and file name), because the two machines may mount the folder at different places. |
| `media[].segments` | when the media is made of segments | *(Level 2)* The media ids it plays, in order; such media have no `ref` or `path` (the files are those of the media listed). |
| `media[].angles` | when the media has angle files | *(Level 2)* One `{ ref, path }` per other angle (angle 2, 3, …), `path` as for the media itself, so apps can match those files too. |
| `modified` | yes | When the manifest last changed (ISO 8601). |

The index MUST NOT list menu asset paths; the server keeps its allowlist to itself. Apps MUST
ignore fields they don't know.

**Caching.** Responses SHOULD carry an `ETag` and `Cache-Control: private, no-cache`, and answer
`If-None-Match` with `304`, so an unchanged index costs one small request. Servers SHOULD gzip the
index when the app accepts it. `?refresh` asks the server to rescan before answering. Without it,
servers answer from their latest scan and SHOULD rescan in the background.

### `GET {B}/v1/files/{root}/{path}`

Signed in. One manifest, or one menu asset a manifest names.

- The server MUST serve **only** the manifests in its index and the menu assets they name (the
  assets `menuAssets()` in [`packages/core`](../packages/core/src/assets.ts) returns). Everything
  else, in particular media files, MUST get `404`, the same answer as for a file that doesn't exist.
- Paths MUST NOT escape their folder (`..`, absolute paths).
- **Range requests MUST be supported**, because players seek in motion menus and transitions.
- `Content-Type` SHOULD match the file. Servers MAY let apps cache art briefly
  (`Cache-Control: private, max-age=60`).
- **Bundles** (format spec §2.1) are served as if they were folders: the manifest at
  `{path to bundle}/manifest.json` and each asset at `{path to bundle}/{asset path}`, for example
  `/v1/files/0/Movies/The%20Matrix%20(1999)/The%20Matrix%20(1999).ovmz/menus/main.jpg`. Relative
  asset URLs then resolve inside the bundle on their own. Range requests work the same way (members
  are stored uncompressed, so a range maps straight onto the bundle file). The index lists a bundle
  with `path` = the bundle's own path and `url` = its `manifest.json`; `media` paths are relative to
  the folder that holds the bundle, as for a plain manifest.

## 5. Network

Standalone servers SHOULD answer only home-network and VPN addresses by default (loopback,
RFC 1918, link-local, IPv6 unique-local, 100.64.0.0/10). Native servers follow their own
remote-access settings.

## 6. Versions

- The path carries the API major version (`/v1/`) and `health.api` repeats it.
- Within v1, changes are additive: new optional fields and new endpoints only.
- A breaking change would be `/v2/`, served alongside `/v1/`.

## 7. Planned

- **Resolved media:** native servers know which of their items each `media[].path` is. They could
  return that directly (e.g. `"item": {"scheme": "jellyfin", "id": "…"}`), so apps skip path
  matching. It would be an optional additive field. 
