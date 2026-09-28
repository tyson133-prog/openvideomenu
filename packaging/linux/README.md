# Running the asset server on Linux

No port is needed: the asset server is plain Node.js. Choose Docker (below, or
[`docker/compose.yaml`](../../docker/compose.yaml)) or a systemd service.

## As a systemd service

Needs Node.js 22 or newer (`node --version`).

```sh
# 1. Build it (from a checkout of this repository) and copy it into place.
npm ci && npm run build
sudo mkdir -p /opt/openvideomenu
sudo cp -r package.json package-lock.json node_modules packages /opt/openvideomenu/

# 2. A user of its own. It only needs to READ your media: add it to the group that owns the media
#    (for example the plex group), or grant read access another way.
sudo useradd --system --no-create-home --shell /usr/sbin/nologin openvideomenu
sudo usermod -aG plex openvideomenu   # if your media is readable by the plex group

# 3. Settings.
sudo mkdir -p /etc/openvideomenu
sudo cp packaging/linux/asset-server.env.example /etc/openvideomenu/asset-server.env
sudo nano /etc/openvideomenu/asset-server.env   # MEDIA_ROOTS, PLEX_URL and/or JELLYFIN_URL

# 4. The service.
sudo cp packaging/linux/openvideomenu-asset-server.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now openvideomenu-asset-server
```

Check it:

```sh
systemctl status openvideomenu-asset-server
journalctl -u openvideomenu-asset-server -f
curl http://localhost:8790/v1/health
```

The service starts at boot, restarts if it stops unexpectedly, and runs locked down: it can read your
files but write nothing (`ProtectSystem=strict`, `ProtectHome=read-only`).

## Discovery

TV apps find the server by itself through mDNS (`_openvideomenu._tcp`) on the same network. If they
don't (another subnet, a firewall, or a container without host networking), enter the address in the
app: `http://<server>:8790`. Open UDP 5353 (mDNS) and TCP 8790 if the host runs a firewall.
