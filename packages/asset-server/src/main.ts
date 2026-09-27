#!/usr/bin/env node
import { hostname } from "node:os";
import { FORMAT_NAME } from "@menustream/core";
import { AnyOf, JellyfinTokenCheck, NoAuth, PlexTokenCheck, type TokenCheck } from "./auth.js";
import { Catalog } from "./catalog.js";
import { advertise } from "./discovery.js";
import { createAssetServer } from "./server.js";

const USAGE = `${FORMAT_NAME} asset server (proof of concept)

Serves menu manifests and menu art from your media folders to TV apps on your home network.
Read-only. Serves only manifests and the menu art they name, never your media.

Usage:
  menustream-asset-server --media <folder> [--media <folder> ...] --plex-url <url> [options]
  menustream-asset-server --media <folder> [--media <folder> ...] --jellyfin-url <url> [options]

Options (or environment variables):
  --media <folder>     MEDIA_ROOTS  (comma-separated)   Media folders to scan (mount them read-only)
  --plex-url <url>     PLEX_URL     e.g. http://192.168.1.20:32400   Plex sign-ins are checked against this server
  --jellyfin-url <url> JELLYFIN_URL e.g. http://192.168.1.20:8096    Jellyfin sign-ins are checked against this server
                       (give one or both)
  --port <n>           PORT         default 8790
  --name <text>        SERVER_NAME  name shown in TV apps (default: this machine's name)
  --no-discovery       DISCOVERY=off   don't announce on the network (mDNS)
  --allow-remote       ALLOW_REMOTE=1  also answer non-local addresses (not recommended)
  --no-auth            AUTH=none       skip token checks (local testing only)
`;

function options(argv: string[]) {
  const values = (name: string) => argv.flatMap((a, i) => (a === name && argv[i + 1] ? [argv[i + 1]!] : []));
  const flag = (name: string) => argv.includes(name);
  const env = process.env;
  const media = [...values("--media"), ...(env.MEDIA_ROOTS ?? "").split(",").map((s) => s.trim()).filter(Boolean)];
  return {
    help: flag("--help") || flag("-h"),
    media,
    plexUrl: values("--plex-url")[0] ?? env.PLEX_URL,
    jellyfinUrl: values("--jellyfin-url")[0] ?? env.JELLYFIN_URL,
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
  if (o.media.length === 0) throw new Error(`No media folders given.\n\n${USAGE}`);

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
  const scanStarted = Date.now();
  void catalog.start().then(() => {
    const found = catalog.entries();
    const invalid = found.filter((e) => !e.valid);
    console.log(
      `Found ${found.length} manifest(s) in ${o.media.join(", ")} in ${((Date.now() - scanStarted) / 1000).toFixed(1)} s` +
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
  if (stopAdvertising) console.log(`Announcing "${o.name}" as _menustream._tcp`);

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
