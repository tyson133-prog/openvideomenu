#!/bin/sh
# Puts the public demo account's password back to the one on the website, in case a visitor changed it.
# Run by cron every 15 minutes (see README.md). Needs /opt/ovm-demo/reset.env (mode 600):
#   OVM_DEMO_API_KEY=<the key configure.mjs printed>
set -eu
. /opt/ovm-demo/reset.env
base=http://127.0.0.1:8096
auth="Authorization: MediaBrowser Token=\"$OVM_DEMO_API_KEY\""

# Jellyfin stopped: nothing to do.
curl -sf -m 5 "$base/health" >/dev/null || exit 0

id=$(curl -sf -m 10 -H "$auth" "$base/Users" | python3 -c 'import sys, json; print(next(u["Id"] for u in json.load(sys.stdin) if u["Name"] == "demo"))')
curl -sf -m 10 -X POST -H "$auth" -H "Content-Type: application/json" -d '{"NewPw":"demo"}' "$base/Users/$id/Password"
