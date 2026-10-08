#!/bin/sh
# Installs (or updates, or removes) the Open Video Menu menu server on Linux as a systemd service.
#
#   curl -fsSL https://openvideomenu.org/install/menu-server.sh | sudo sh
#   curl -fsSL https://openvideomenu.org/install/menu-server.sh | sudo sh -s -- --uninstall
#
# It downloads the latest release (the server with its own Node.js) to /opt/openvideomenu-menu-server,
# runs it as the system user "openvideomenu", and keeps its settings in /etc/openvideomenu/menu-server.env.
# Run it again to update; your settings are kept. Without a terminal to ask in, it takes the settings from
# OVM_PLEX_URL and OVM_PLEX_TOKEN (or OVM_JELLYFIN_URL and OVM_JELLYFIN_API_KEY).
#
# This file is site/install/menu-server.sh too; the release workflow checks the two are the same.
set -eu

REPO="tyson133-prog/openvideomenu"
APP=/opt/openvideomenu-menu-server
CONF_DIR=/etc/openvideomenu
CONF="$CONF_DIR/menu-server.env"
UNIT=/etc/systemd/system/openvideomenu-menu-server.service
USER_NAME=openvideomenu

say() { printf '%s\n' "$*"; }
fail() { printf 'Error: %s\n' "$*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || fail "run it as root (with sudo)."
command -v systemctl >/dev/null 2>&1 || fail "this installer needs systemd. Use the Docker image instead (see openvideomenu.org)."

if [ "${1:-}" = "--uninstall" ]; then
  systemctl disable --now openvideomenu-menu-server 2>/dev/null || true
  rm -f "$UNIT"
  systemctl daemon-reload
  rm -rf "$APP" "$CONF_DIR"
  if id "$USER_NAME" >/dev/null 2>&1; then userdel "$USER_NAME" 2>/dev/null || true; fi
  say "The menu server is removed (program, settings, service and the $USER_NAME user)."
  exit 0
fi

case "$(uname -m)" in
  x86_64 | amd64) ARCH=x64 ;;
  aarch64 | arm64) ARCH=arm64 ;;
  *) fail "no download for $(uname -m) yet. Use the Docker image, or run it with Node.js (see openvideomenu.org)." ;;
esac

# --- the program -----------------------------------------------------------------------------------
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
TARBALL="${OVM_TARBALL:-}"
if [ -z "$TARBALL" ]; then
  URL="https://github.com/$REPO/releases/latest/download/openvideomenu-menu-server-linux-$ARCH.tar.gz"
  say "Downloading the menu server ($ARCH)..."
  if command -v curl >/dev/null 2>&1; then curl -fsSL "$URL" -o "$TMP/server.tar.gz"
  elif command -v wget >/dev/null 2>&1; then wget -qO "$TMP/server.tar.gz" "$URL"
  else fail "needs curl or wget to download."; fi
  TARBALL="$TMP/server.tar.gz"
fi
tar -xzf "$TARBALL" -C "$TMP"
[ -f "$TMP/openvideomenu-menu-server/bin/node" ] || fail "the download isn't the menu server."
chmod 755 "$TMP/openvideomenu-menu-server/bin/node"

UPDATE=no
[ -d "$APP" ] && UPDATE=yes
rm -rf "$APP.new"
mv "$TMP/openvideomenu-menu-server" "$APP.new"
rm -rf "$APP"
mv "$APP.new" "$APP"
chown -R root:root "$APP"
chmod -R go-w "$APP"

# --- its user: reads the media, nothing else ---------------------------------------------------------
if ! id "$USER_NAME" >/dev/null 2>&1; then
  useradd --system --no-create-home --shell /usr/sbin/nologin "$USER_NAME"
fi
# Plex's and Jellyfin's own users usually own the media's group: reading what they read is enough.
for g in plex jellyfin; do
  if getent group "$g" >/dev/null 2>&1; then usermod -aG "$g" "$USER_NAME"; fi
done

# --- settings: asked once, kept on updates ------------------------------------------------------------
ask() { # ask "Question" default -> answer on stdout (from the terminal: stdin is the download)
  printf '%s' "$1" >/dev/tty
  [ -n "$2" ] && printf ' [%s]' "$2" >/dev/tty
  printf ': ' >/dev/tty
  read -r answer </dev/tty || answer=""
  printf '%s' "${answer:-$2}"
}

if [ ! -f "$CONF" ]; then
  mkdir -p "$CONF_DIR"
  KIND="" URL_IN="" TOKEN_IN=""
  if [ -n "${OVM_PLEX_URL:-}${OVM_PLEX_TOKEN:-}" ]; then
    KIND=plex URL_IN="${OVM_PLEX_URL:-http://127.0.0.1:32400}" TOKEN_IN="${OVM_PLEX_TOKEN:-}"
  elif [ -n "${OVM_JELLYFIN_URL:-}${OVM_JELLYFIN_API_KEY:-}" ]; then
    KIND=jellyfin URL_IN="${OVM_JELLYFIN_URL:-http://127.0.0.1:8096}" TOKEN_IN="${OVM_JELLYFIN_API_KEY:-}"
  elif ( : </dev/tty ) 2>/dev/null; then
    case "$(ask "Is your media server Plex or Jellyfin? (p/j)" p)" in
      j* | J*) KIND=jellyfin ;;
      *) KIND=plex ;;
    esac
    if [ "$KIND" = plex ]; then
      URL_IN="$(ask "Plex address" http://127.0.0.1:32400)"
      # Plex on this machine keeps its token in Preferences.xml: offer it.
      PREFS="/var/lib/plexmediaserver/Library/Application Support/Plex Media Server/Preferences.xml"
      FOUND=""
      [ -r "$PREFS" ] && FOUND="$(sed -n 's/.*PlexOnlineToken="\([^"]*\)".*/\1/p' "$PREFS" | head -n1)"
      if [ -n "$FOUND" ]; then
        case "$(ask "Found the token of the Plex server on this machine. Use it? (y/n)" y)" in
          n* | N*) TOKEN_IN="$(ask "Plex token" "")" ;;
          *) TOKEN_IN="$FOUND" ;;
        esac
      else
        say "Your Plex token: in Plex Web open any film, then ... > Get Info > View XML; the address ends in X-Plex-Token=..." >/dev/tty
        TOKEN_IN="$(ask "Plex token" "")"
      fi
    else
      URL_IN="$(ask "Jellyfin address" http://127.0.0.1:8096)"
      say "A Jellyfin API key: Dashboard > API Keys > +." >/dev/tty
      TOKEN_IN="$(ask "Jellyfin API key" "")"
    fi
  fi

  umask 077
  {
    say "# Settings for openvideomenu-menu-server.service (written by install.sh; see menu-server.env.example"
    say "# in $APP for all of them). Readable by root only: it holds a token."
    if [ "$KIND" = jellyfin ]; then
      say "JELLYFIN_URL=$URL_IN"
      say "JELLYFIN_API_KEY=$TOKEN_IN"
    else
      say "PLEX_URL=${URL_IN:-http://127.0.0.1:32400}"
      say "PLEX_TOKEN=$TOKEN_IN"
    fi
    say "#PATH_MAP="
    say "#MEDIA_ROOTS="
    say "SERVER_NAME=\"Open Video Menu on $(hostname)\""
  } >"$CONF"
  chown root:root "$CONF"
  chmod 600 "$CONF"
  [ -n "$TOKEN_IN" ] || say "No token yet: add it to $CONF, then run: systemctl restart openvideomenu-menu-server"
fi

# --- the service -------------------------------------------------------------------------------------
cp "$APP/openvideomenu-menu-server.service" "$UNIT"
systemctl daemon-reload
systemctl enable openvideomenu-menu-server >/dev/null 2>&1
systemctl restart openvideomenu-menu-server

PORT="$(sed -n 's/^PORT=\([0-9]*\).*/\1/p' "$CONF" | tail -n1)"
PORT="${PORT:-8790}"
for _ in 1 2 3 4 5 6 7 8 9 10; do
  if command -v curl >/dev/null 2>&1; then
    HEALTH="$(curl -fsS "http://127.0.0.1:$PORT/v1/health" 2>/dev/null || true)"
  else
    HEALTH="$(wget -qO- "http://127.0.0.1:$PORT/v1/health" 2>/dev/null || true)"
  fi
  [ -n "$HEALTH" ] && break
  sleep 1
done

[ "$UPDATE" = yes ] && say "The menu server is updated." || say "The menu server is installed."
if [ -n "${HEALTH:-}" ]; then
  say "It answers on port $PORT: $HEALTH"
  say "TV apps on your network find it by themselves."
else
  say "It didn't answer yet. See: journalctl -u openvideomenu-menu-server -e"
fi
say "Settings: $CONF   Remove it: curl -fsSL https://openvideomenu.org/install/menu-server.sh | sudo sh -s -- --uninstall"
