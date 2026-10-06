# Demo server (demo.openvideomenu.org)

A Jellyfin server with the Open Video Menu plugin and the two example titles (Sintel and the Example
Show), reachable from the internet. App-store reviewers sign in to it, since ovmplayer has no
content of its own, and anyone can use it to try the apps without a library of their own.

It holds only the examples built in [`examples/`](../../examples): freely licensed or made for this
project. Never anything else.

## Layout on the host

```
/opt/ovm-demo/
  compose.yaml                 # this folder's compose.yaml
  config/plugins/OpenVideoMenu_<version>/   # the plugin's two DLLs + meta.json
  cache/
  media/Movies/Sintel (2010)/  # examples/sintel: film, manifest, menus/, Trailers/, Other/
  media/TV/Example Show/       # examples/dvd-shaped: manifest, menus/, Season 00/, Season 01/
```

Jellyfin listens on `127.0.0.1:8096` only. The host's web server publishes it: `apache-demo.conf` is
the Apache site (proxy to localhost), and `certbot --apache -d demo.openvideomenu.org` adds HTTPS.

The container is capped at 400 MB of memory, so on a small host that serves other sites only Jellyfin
can run out.

## Setting it up

1. Build the examples (`node examples/sintel/build.mjs`, `node examples/dvd-shaped/build.mjs`) and the
   plugin (`dotnet build -c Release` in `plugins/jellyfin/Jellyfin.Plugin.OpenVideoMenu`).
2. Copy the layout above to the host, then `docker compose up -d` in `/opt/ovm-demo`.
3. **Before the site is public**, run the first-run setup through an SSH tunnel, so no one else can
   finish Jellyfin's setup wizard first:
   ```sh
   ssh -L 18096:127.0.0.1:8096 <host>
   OVM_DEMO_ADMIN_PASSWORD=… OVM_DEMO_REVIEW_PASSWORD=… node configure.mjs
   ```
   It creates the `admin` account, the Movies and TV libraries, and the `review` account (an
   ordinary user), then lists the titles' IDs. The TV library reads only what's on disk: online
   metadata would match the made-up Example Show to a real show and rename it.
4. Enable the Apache site, then run certbot.

## Running it

```sh
cd /opt/ovm-demo
sudo docker compose up -d     # start
sudo docker compose stop      # stop
```

## For a store review

- Server: `https://demo.openvideomenu.org` (in ovmplayer: Jellyfin → Type an address…)
- Account: `review`, with the password kept outside the repo
- Deep-link test values: `mediaType=movie` with Sintel's Jellyfin ID, and `mediaType=series` with the
  Example Show's ID (`configure.mjs` prints both)
