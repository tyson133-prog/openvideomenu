import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import { FORMAT_NAME, FORMAT_VERSION } from "@openvideomenu/core";
import { isLocalAddress, type Credential, type TokenCheck } from "./auth.js";
import { Catalog } from "./catalog.js";
import { sendFile } from "./files.js";

export const API_VERSION = "1";

export interface AssetServerOptions {
  catalog: Catalog;
  auth: TokenCheck;
  /** Also answer non-local addresses. Off by default: this is a home-network service. */
  allowRemote?: boolean;
  log?: (line: string) => void;
}

const json = (res: ServerResponse, status: number, body: unknown) =>
  void res
    .writeHead(status, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" })
    .end(JSON.stringify(body));

/**
 * The viewer's sign-in, from headers only (never URLs): a Plex token in X-Plex-Token (or a Bearer
 * Authorization header), or a Jellyfin token in its own `Authorization: MediaBrowser ..., Token="..."`.
 */
export function credentialOf(req: IncomingMessage): Credential | undefined {
  const plex = req.headers["x-plex-token"];
  if (typeof plex === "string" && plex) return { kind: "plex", token: plex };
  const auth = req.headers.authorization;
  if (auth?.startsWith("Bearer ")) return { kind: "plex", token: auth.slice(7) };
  const jellyfin = auth?.startsWith("MediaBrowser ") ? /\bToken="([^"]+)"/.exec(auth) : null;
  if (jellyfin) return { kind: "jellyfin", token: jellyfin[1]! };
  return undefined;
}

/**
 * GET /v1/health          unauthenticated: {ok, name, api, format, manifests, auth}
 * GET /v1/index           the manifests and the media they reference (gzip when asked; ETag, so an
 *                         unchanged index answers 304). ?refresh rescans first.
 * GET /v1/files/<root>/<path>   a manifest or one of its menu assets (nothing else)
 */
export function createAssetServer({ catalog, auth, allowRemote = false, log = () => {} }: AssetServerOptions): Server {
  return createServer((req, res) => {
    void handle(req, res).catch((e) => {
      log(`error ${req.method} ${req.url}: ${(e as Error).message}`);
      if (!res.headersSent) json(res, 500, { error: "Internal error" });
    });
  });

  async function handle(req: IncomingMessage, res: ServerResponse) {
    if (!allowRemote && !isLocalAddress(req.socket.remoteAddress)) {
      log(`refused non-local ${req.socket.remoteAddress}`);
      return json(res, 403, { error: "This server only answers the local network." });
    }
    if (req.method !== "GET" && req.method !== "HEAD") return json(res, 405, { error: "Read-only server." });

    const url = new URL(req.url ?? "/", "http://localhost");
    const path = url.pathname;

    if (path === "/v1/health")
      return json(res, 200, {
        ok: true,
        name: `${FORMAT_NAME} asset server`,
        api: API_VERSION,
        format: FORMAT_VERSION,
        manifests: catalog.entries().length,
        scanning: !catalog.isReady,
        auth: auth.description,
      });

    if (!(await auth.allows(credentialOf(req)))) return json(res, 401, { error: "A valid token for this media server is required." });

    if (path === "/v1/index") {
      await (url.searchParams.has("refresh") ? catalog.scan() : catalog.ready());
      const body = catalog.index({ api: API_VERSION, format: FORMAT_VERSION, roots: catalog.roots.length });
      const headers: Record<string, string> = {
        "Content-Type": "application/json; charset=utf-8",
        // Apps may keep a copy but must check it's current each time (a cheap 304 when it is).
        "Cache-Control": "private, no-cache",
        ETag: body.etag,
        Vary: "Accept-Encoding",
      };
      if (req.headers["if-none-match"] === body.etag) return void res.writeHead(304, headers).end();
      const gzip = String(req.headers["accept-encoding"] ?? "").includes("gzip");
      const payload = gzip ? body.gzip : body.json;
      res.writeHead(200, { ...headers, ...(gzip ? { "Content-Encoding": "gzip" } : {}), "Content-Length": String(Buffer.byteLength(payload)) });
      return void res.end(req.method === "HEAD" ? undefined : payload);
    }

    const m = /^\/v1\/files\/(\d+)\/(.+)$/.exec(path);
    if (m) {
      let rel: string;
      try {
        rel = m[2]!.split("/").map(decodeURIComponent).join("/");
      } catch {
        return json(res, 400, { error: "Bad path." });
      }
      await catalog.ready();
      const file = catalog.resolveFile(Number(m[1]), rel);
      // 404 for anything not allowlisted, so the server doesn't confirm which other files exist.
      if (!file) return json(res, 404, { error: "Not found." });
      try {
        return sendFile(req, res, file, { "Cache-Control": "private, max-age=60" });
      } catch {
        return json(res, 404, { error: "Not found." });
      }
    }
    json(res, 404, { error: "Not found." });
  }
}
