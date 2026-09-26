#!/usr/bin/env node
import { hostname } from "node:os";
import { FORMAT_NAME } from "@menustream/core";
import { NoAuth, PlexTokenCheck, type TokenCheck } from "./auth.js";
import { Catalog } from "./catalog.js";
import { advertise } from "./discovery.js";
import { createAssetServer } from "./server.js";

const USAGE = `${FORMAT_NAME} asset server (proof of concept)

Serves menu manifests and menu art from your media folders to TV apps on your home network.
Read-only. Serves only manifests and the menu art they name, never your media.

Usage:
  menustream-asset-server --media <folder> [--media <folder> ...] --plex-url <url> [options]

Options (or environment variables):
  --media <folder>     MEDIA_ROOTS  (comma-separated)   Media folders to scan (mount them read-only)
  --plex-url <url>     PLEX_URL     e.g. http://192.168.1.20:32400   Tokens are checked against this server
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
    if (!o.plexUrl) throw new Error(`--plex-url (or PLEX_URL) is required so tokens can be checked.\n\n${USAGE}`);
    const plex = new PlexTokenCheck(o.plexUrl);
    try {
      console.log(`Plex server id: ${await plex.serverId()}`);
    } catch (e) {
      console.warn(`WARNING: couldn't reach Plex yet (${(e as Error).message}); will retry on each request.`);
    }
    auth = plex;
  }

  const catalog = new Catalog(o.media);
  const found = catalog.entries(true);
  const invalid = found.filter((e) => !e.valid);
  console.log(`Found ${found.length} manifest(s) in ${o.media.join(", ")}${invalid.length ? ` (${invalid.length} with problems)` : ""}`);
  for (const e of invalid) console.warn(`  ${e.path}: ${e.errors?.[0]}`);
  for (const e of found) for (const w of e.warnings ?? []) console.warn(`  ${e.path}: ${w}`);

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
