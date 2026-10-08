# Running the menu server on Linux

Choose the one-command install (a systemd service, below) or Docker
([`docker/compose.yaml`](../../docker/compose.yaml), image `ghcr.io/tyson133-prog/openvideomenu-menu-server`).

## One-command install

```sh
curl -fsSL https://openvideomenu.org/install/menu-server.sh | sudo sh
```

It downloads the latest release (the server with its own Node.js, x64 or arm64), installs it to
`/opt/openvideomenu-menu-server`, runs it as the system user `openvideomenu`, and asks for your Plex or
Jellyfin address and token. On a machine running Plex it finds Plex's token itself. Run it again to update;
your settings are kept. To remove everything:

```sh
curl -fsSL https://openvideomenu.org/install/menu-server.sh | sudo sh -s -- --uninstall
```

The script is [`install.sh`](install.sh); the website serves the same file.

## Settings

`/etc/openvideomenu/menu-server.env`, readable by root only, because it holds a token (systemd reads it for
the service). All settings: [`menu-server.env.example`](menu-server.env.example).

- **`PLEX_URL` and `PLEX_TOKEN`** (or `JELLYFIN_URL` and `JELLYFIN_API_KEY`): with the token, the server reads
  the library folders from your media server and follows them when you add a library. TV apps' sign-ins are
  checked against it too.
- **`PATH_MAP`**, when the media server sees the folders under other paths than this machine (Plex in a
  container sees `/data`, here it's `/mnt/media`): `PATH_MAP=/data=/mnt/media`.
- **`MEDIA_ROOTS`**: extra folders to scan besides the libraries (optional).

After changing them: `sudo systemctl restart openvideomenu-menu-server`.

The server only needs to read your media. The installer adds its user to the `plex` and `jellyfin` groups when
they exist, which usually owns the media; otherwise grant it read access another way.

## Without the installer

The same steps by hand, from a release download (`openvideomenu-menu-server-linux-x64.tar.gz` or `-arm64`):

```sh
sudo tar -xzf openvideomenu-menu-server-linux-x64.tar.gz -C /opt   # → /opt/openvideomenu-menu-server
sudo useradd --system --no-create-home --shell /usr/sbin/nologin openvideomenu
sudo install -d /etc/openvideomenu
sudo install -m 600 /opt/openvideomenu-menu-server/menu-server.env.example /etc/openvideomenu/menu-server.env
sudo nano /etc/openvideomenu/menu-server.env    # PLEX_URL and PLEX_TOKEN, or Jellyfin's
sudo cp /opt/openvideomenu-menu-server/openvideomenu-menu-server.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now openvideomenu-menu-server
```

The downloads are built by [`build.sh`](build.sh) (the bundled server and the official Node.js 24 binary,
checked against nodejs.org's checksums).

## Check it

```sh
systemctl status openvideomenu-menu-server
journalctl -u openvideomenu-menu-server -f
curl http://localhost:8790/v1/health
```

The service starts at boot, restarts if it stops unexpectedly, and runs locked down: it can read your
files but write nothing (`ProtectSystem=strict`, `ProtectHome=read-only`).

## Discovery

TV apps find the server by itself through mDNS (`_openvideomenu._tcp`) on the same network. If they
don't (another subnet, a firewall, or a container without host networking), enter the address in the
app: `http://<server>:8790`. Open UDP 5353 (mDNS) and TCP 8790 if the host runs a firewall.
