#!/usr/bin/env bash
# Builds the Linux downloads of the menu server: one tarball per architecture, each with its own Node.js
# (the official Node 24 LTS binary, checked against nodejs.org's SHA-256 list) and the bundled server.
#
#   bash packaging/linux/build.sh            # after npm ci, from the repository root
#
# Writes packaging/build/out/openvideomenu-menu-server-linux-x64.tar.gz and -linux-arm64.tar.gz.
# NODE_DIST overrides where Node comes from (default: the latest Node 24 release).
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
out="$root/packaging/build/out"
work="$root/packaging/build/linux"
dist="${NODE_DIST:-https://nodejs.org/dist/latest-v24.x}"
mkdir -p "$out"
rm -rf "$work" && mkdir -p "$work"

# The server, as one file (the bundle reads @openvideomenu/core from its build output).
(cd "$root" && npx tsc -b packages/core && node packaging/bundle.mjs)

curl -fsSL "$dist/SHASUMS256.txt" -o "$work/SHASUMS256.txt"
for arch in x64 arm64; do
  name="$(grep -oE "node-v[0-9.]+-linux-$arch\.tar\.xz" "$work/SHASUMS256.txt" | head -n1)"
  [ -n "$name" ] || { echo "No Node build for linux-$arch in $dist" >&2; exit 1; }
  curl -fsSL "$dist/$name" -o "$work/$name"
  (cd "$work" && grep " $name\$" SHASUMS256.txt | sha256sum -c -)

  pkg="$work/$arch/openvideomenu-menu-server"
  mkdir -p "$pkg/bin"
  tar -xJf "$work/$name" -C "$work/$arch" --strip-components=1 --wildcards "*/bin/node" "*/LICENSE"
  mv "$work/$arch/bin/node" "$pkg/bin/node"
  chmod 755 "$pkg/bin/node"
  mv "$work/$arch/LICENSE" "$pkg/NODE-LICENSE"
  cp "$root/packaging/build/menu-server.mjs" "$pkg/"
  cp "$root/packaging/linux/openvideomenu-menu-server.service" "$root/packaging/linux/menu-server.env.example" "$pkg/"
  cp "$root/LICENSE" "$root/NOTICE" "$pkg/"
  echo "${name#node-}" | sed 's/-linux.*//' > "$pkg/NODE-VERSION"
  tar -czf "$out/openvideomenu-menu-server-linux-$arch.tar.gz" -C "$work/$arch" --owner=0 --group=0 --mode=go-w openvideomenu-menu-server
  echo "$out/openvideomenu-menu-server-linux-$arch.tar.gz"
done
