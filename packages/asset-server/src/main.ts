#!/usr/bin/env node
import { readFileSync } from "node:fs";
import { hostname } from "node:os";
import { FORMAT_NAME } from "@openvideomenu/core";
import { AnyOf, JellyfinTokenCheck, NoAuth, PlexTokenCheck, type TokenCheck } from "./auth.js";
import { Catalog } from "./catalog.js";
import { advertise } from "./discovery.js";
import { JellyfinLibrary, LibraryFollower, parseEnvFile, parsePathMap, PlexLibrary, type LibrarySource } from "./library.js";
import { createAssetServer } from "./server.js";

const USAGE = `${FORMAT_NAME} asset server (proof of concept)

Serves menu manifests and menu art from your media folders to TV apps on your home network.
Read-only. Serves only manifests and the menu art they name, never your media.

Usage:
  openvideomenu-asset-server --plex-url <url> --plex-token <token> [options]
  openvideomenu-asset-server --jellyfin-url <url> --jellyfin-api-key <key> [options]
  openvideomenu-asset-server --media <folder> [--media <folder> ...] --plex-url <url> [options]

With a token it reads the media server's library folders and follows them; without one, give the
folders with --media.

Options (or environment variables):
  --plex-url <url>          PLEX_URL          e.g. http://192.168.1.20:32400   Plex sign-ins are checked against it
  --plex-token <token>      PLEX_TOKEN        read Plex's library folders with this token
  --jellyfin-url <url>      JELLYFIN_URL      e.g. http://192.168.1.20:8096    Jellyfin sign-ins are checked against it
  --jellyfin-api-key <key>  JELLYFIN_API_KEY  read Jellyfin's library folders with this API key
                            (give Plex, Jellyfin or both)
  --media <folder>          MEDIA_ROOTS (comma-separated)  more folders to scan (mount them read-only)
  --path-map <from>=<to>    PATH_MAP (";"-separated)  a library path as this machine sees it,
                            e.g. /data=/mnt/media (Plex in Docker)
  --config <file>           OVM_CONFIG        read these settings from a KEY=VALUE file
  --port <n>           PORT         default 8790
  --name <text>        SERVER_NAME  name shown in TV apps (default: this machine's name)
  --no-discovery       DISCOVERY=off   don't announce on the network (mDNS)
  --allow-remote       ALLOW_REMOTE=1  also answer non-local addresses (not recommended)
  --no-auth            AUTH=none       skip token checks (local testing only)
`;

function options(argv: string[]) {
  const values = (name: string) => argv.flatMap((a, i) => (a === name && argv[i + 1] ? [argv[i + 1]!] : []));
  const flag = (name: string) => argv.includes(name);
  // A settings file fills in what the environment doesn't set; command-line flags win over both.
  const config = values("--config")[0] ?? process.env.OVM_CONFIG;
  const env: Record<string, string | undefined> = { ...(config ? parseEnvFile(readFileSync(config, "utf8")) : {}), ...process.env };
  const list = (v: string | undefined, sep: string) => (v ?? "").split(sep).map((s) => s.trim()).filter(Boolean);
  return {
    help: flag("--help") || flag("-h"),
    media: [...values("--media"), ...list(env.MEDIA_ROOTS, ",")],
    pathMap: parsePathMap([...values("--path-map"), ...list(env.PATH_MAP, ";")]),
    plexUrl: values("--plex-url")[0] ?? env.PLEX_URL,
    plexToken: values("--plex-token")[0] ?? env.PLEX_TOKEN,
    jellyfinUrl: values("--jellyfin-url")[0] ?? env.JELLYFIN_URL,
    jellyfinApiKey: values("--jellyfin-api-key")[0] ?? env.JELLYFIN_API_KEY,
    port: Number(values("--port")[0] ?? env.PORT ?? 8790),
    name: values("--name")[0] ?? env.SERVER_NAME ?? `${FORMAT_NAME} on ${hostname()}`,
    discovery: !flag("--no-discovery") && env.DISCOVERY !== "off",
    allowRemote: flag("--allow-remote") || env.ALLOW_REMOTE === "1",
    noAuth: flag("--no-auth") || env.AUTH === "none",
  };
}

async function main() {
  const o = options(process.argv.slice(2));
  if (o.help) return void console.log(USAGE);
  if (o.plexToken && !o.plexUrl) throw new Error(`--plex-token needs --plex-url.\n\n${USAGE}`);
  if (o.jellyfinApiKey && !o.jellyfinUrl) throw new Error(`--jellyfin-api-key needs --jellyfin-url.\n\n${USAGE}`);
  const sources: LibrarySource[] = [];
  if (o.plexUrl && o.plexToken) sources.push(new PlexLibrary(o.plexUrl, o.plexToken));
  if (o.jellyfinUrl && o.jellyfinApiKey) sources.push(new JellyfinLibrary(o.jellyfinUrl, o.jellyfinApiKey));
  if (sources.length === 0 && o.media.length === 0) {
    throw new Error(`No media folders: give a Plex token or a Jellyfin API key (to follow the libraries), or --media.\n\n${USAGE}`);
  }

  let auth: TokenCheck;
  if (o.noAuth) {
    auth = new NoAuth();
    console.warn("WARNING: token checks are off (--no-auth). Use this only for local testing.");
  } else {
    if (!o.plexUrl && !o.jellyfinUrl) throw new Error(`--plex-url or --jellyfin-url is required so sign-ins can be checked.\n\n${USAGE}`);
    const checks: TokenCheck[] = [];
    if (o.plexUrl) {
      const plex = new PlexTokenCheck(o.plexUrl);
      try {
        console.log(`Plex server id: ${await plex.serverId()}`);
      } catch (e) {
        console.warn(`WARNING: couldn't reach Plex yet (${(e as Error).message}); will retry on each request.`);
      }
      checks.push(plex);
    }
    if (o.jellyfinUrl) {
      try {
        const info = (await (await fetch(new URL("/System/Info/Public", o.jellyfinUrl))).json()) as { ServerName?: string; Version?: string };
        console.log(`Jellyfin server: ${info.ServerName} (${info.Version})`);
      } catch (e) {
        console.warn(`WARNING: couldn't reach Jellyfin yet (${(e as Error).message}); will retry on each request.`);
      }
      checks.push(new JellyfinTokenCheck(o.jellyfinUrl));
    }
    auth = checks.length === 1 ? checks[0]! : new AnyOf(checks);
  }

  // The first scan runs in the background: the server answers health checks at once, and index
  // requests wait for the scan instead of the whole process blocking on a slow share.
  const catalog = new Catalog(o.media);
  catalog.onWarnings = (entries) => {
    for (const e of entries) for (const w of e.warnings ?? []) console.warn(`  ${e.path}: ${w}`);
  };
  // With a token, the media server's libraries decide the folders (re-read every 5 minutes).
  if (sources.length > 0) await new LibraryFollower(sources, o.media, o.pathMap, catalog).start();
  const scanStarted = Date.now();
  void catalog.start().then(() => {
    const found = catalog.entries();
    const invalid = found.filter((e) => !e.valid);
    console.log(
      `Found ${found.length} manifest(s) in ${catalog.roots.join(", ") || "no folders yet"} in ${((Date.now() - scanStarted) / 1000).toFixed(1)} s` +
        (invalid.length ? ` (${invalid.length} with problems)` : ""),
    );
    for (const e of invalid) console.warn(`  ${e.path}: ${e.errors?.[0]}`);
  });

  const server = createAssetServer({ catalog, auth, allowRemote: o.allowRemote, log: (l) => console.log(l) });
  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(o.port, "0.0.0.0", resolve);
  });
  console.log(`Listening on port ${o.port} (auth: ${auth.description}${o.allowRemote ? ", remote allowed" : ", local network only"})`);

  const stopAdvertising = o.discovery ? advertise(o.port, o.name) : undefined;
  if (stopAdvertising) console.log(`Announcing "${o.name}" as _openvideomenu._tcp`);

  const shutdown = async () => {
    await stopAdvertising?.();
    server.close(() => process.exit(0));
    setTimeout(() => process.exit(0), 2000).unref();
  };
  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
}

main().catch((e) => {
  console.error(e instanceof Error ? e.message : e);
  process.exit(1);
});
