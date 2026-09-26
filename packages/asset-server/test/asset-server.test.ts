import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { afterAll, beforeAll, describe, expect, it, vi } from "vitest";
import { Catalog, createAssetServer, isLocalAddress, NoAuth, PlexTokenCheck, type TokenCheck } from "../src/index.js";

const MANIFEST = {
  format: "menustream",
  version: "0.2",
  title: { name: "Movie", year: 2020 },
  media: {
    feature: { ref: "Movie (2020).mp4", kind: "feature" },
    trailer: { ref: "Trailers/Trailer.mp4", kind: "trailer" },
    web: { ref: "https://example.com/a.m3u8" },
  },
  menus: {
    main: {
      canvas: { width: 1280, height: 720 },
      background: { image: "menus/bg.jpg", video: { src: "menus/loop.mp4", loopStart: 2 } },
      buttons: [
        {
          id: "play",
          rect: [0, 0, 100, 40],
          states: { selected: { image: "menus/sel.png" } },
          action: { type: "goto", menu: "main", transition: "menus/whoosh.mp4" },
        },
      ],
    },
  },
  rootMenu: "main",
  fallback: [{ label: "Movie", items: [{ label: "Play", action: { type: "play", media: "feature" } }] }],
};

let root: string;
const write = (rel: string, content: string | Buffer = "x") => {
  const full = join(root, ...rel.split("/"));
  mkdirSync(dirname(full), { recursive: true });
  writeFileSync(full, content);
};

beforeAll(() => {
  root = mkdtempSync(join(tmpdir(), "ms-assets-"));
  write("Movie (2020)/Movie (2020).menustream.json", JSON.stringify(MANIFEST));
  write("Movie (2020)/Movie (2020).mp4", "FILM");
  write("Movie (2020)/Trailers/Trailer.mp4", "TRAILER");
  write("Movie (2020)/menus/bg.jpg", "JPG");
  write("Movie (2020)/menus/loop.mp4", Buffer.alloc(1000, 7));
  write("Movie (2020)/menus/sel.png", "PNG");
  write("Movie (2020)/menus/whoosh.mp4", "WHOOSH");
  write("Movie (2020)/menus/secret.png", "NOT REFERENCED");
  write("Broken/Broken.menustream.json", "{ nope");
  write("secrets.txt", "top secret");
});
afterAll(() => rmSync(root, { recursive: true, force: true }));

describe("Catalog", () => {
  it("indexes manifests with root-relative media paths and menu assets", () => {
    const entries = new Catalog([root]).entries();
    expect(entries.map((e) => [e.path, e.valid])).toEqual([
      ["Broken/Broken.menustream.json", false],
      ["Movie (2020)/Movie (2020).menustream.json", true],
    ]);
    const movie = entries[1]!;
    expect(movie.title?.name).toBe("Movie");
    expect(movie.hasMenus).toBe(true);
    expect(movie.url).toBe("/v1/files/0/Movie%20(2020)/Movie%20(2020).menustream.json");
    expect(movie.media).toEqual([
      { id: "feature", kind: "feature", label: undefined, ref: "Movie (2020).mp4", path: "Movie (2020)/Movie (2020).mp4" },
      { id: "trailer", kind: "trailer", label: undefined, ref: "Trailers/Trailer.mp4", path: "Movie (2020)/Trailers/Trailer.mp4" },
      { id: "web", kind: undefined, label: undefined, ref: "https://example.com/a.m3u8", path: undefined },
    ]);
    expect(movie.assets).toEqual([
      "Movie (2020)/menus/bg.jpg",
      "Movie (2020)/menus/loop.mp4",
      "Movie (2020)/menus/sel.png",
      "Movie (2020)/menus/whoosh.mp4",
    ]);
    expect(entries[0]!.errors?.[0]).toMatch(/Not valid JSON/);
  });
});

describe("Catalog roots", () => {
  it("serves files under roots given with a trailing separator (UNC shares and drive roots resolve that way)", () => {
    const withSlash = new Catalog([root + "/"]);
    withSlash.entries();
    expect(withSlash.resolveFile(0, "Movie (2020)/menus/bg.jpg")).toBeDefined();
    expect(withSlash.resolveFile(0, "Movie (2020)/Movie (2020).mp4")).toBeUndefined();
    expect(withSlash.resolveFile(0, "../secrets.txt")).toBeUndefined();
  });

  it.runIf(process.platform === "win32")("serves files under a UNC-style root", () => {
    // \\localhost\X$\... is a UNC path to the same folder; it resolves with a trailing separator.
    const drive = root.slice(0, 1);
    const unc = new Catalog([`\\\\localhost\\${drive}$${root.slice(2)}`]);
    try {
      unc.entries(true);
    } catch {
      return; // admin shares disabled on this machine: nothing to test
    }
    if (unc.entries().length === 0) return;
    expect(unc.resolveFile(0, "Movie (2020)/menus/bg.jpg")).toBeDefined();
  });
});

describe("server", () => {
  let base: string;
  let close: () => void;
  const allow: TokenCheck = { description: "test", allows: async (t) => t === "good" };

  beforeAll(async () => {
    const server = createAssetServer({ catalog: new Catalog([root]), auth: allow });
    await new Promise<void>((r) => server.listen(0, "127.0.0.1", r));
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
    close = () => server.close();
  });
  afterAll(() => close());

  const get = (path: string, headers: Record<string, string> = { "X-Plex-Token": "good" }) => fetch(base + path, { headers });

  it("answers health without a token", async () => {
    const res = await get("/v1/health", {});
    expect(res.status).toBe(200);
    expect(((await res.json()) as { manifests: number }).manifests).toBe(2);
  });

  it("requires a valid token for the index and files", async () => {
    expect((await get("/v1/index", {})).status).toBe(401);
    expect((await get("/v1/index", { "X-Plex-Token": "bad" })).status).toBe(401);
    expect((await get("/v1/index", { Authorization: "Bearer good" })).status).toBe(200);
    const index = (await (await get("/v1/index")).json()) as { manifests: unknown[] };
    expect(index.manifests).toHaveLength(2);
  });

  it("serves manifests and the menu art they name", async () => {
    const manifest = await get("/v1/files/0/Movie%20(2020)/Movie%20(2020).menustream.json");
    expect(manifest.status).toBe(200);
    expect(((await manifest.json()) as { title: { name: string } }).title.name).toBe("Movie");
    for (const asset of ["menus/bg.jpg", "menus/sel.png", "menus/whoosh.mp4", "menus/loop.mp4"])
      expect((await get(`/v1/files/0/Movie%20(2020)/${asset}`)).status, asset).toBe(200);
  });

  it("supports range requests on menu video", async () => {
    const res = await get("/v1/files/0/Movie%20(2020)/menus/loop.mp4", { "X-Plex-Token": "good", Range: "bytes=10-19" });
    expect(res.status).toBe(206);
    expect(res.headers.get("content-range")).toBe("bytes 10-19/1000");
    expect((await res.arrayBuffer()).byteLength).toBe(10);
  });

  it("never serves media, unreferenced files, or anything outside the roots", async () => {
    for (const path of [
      "/v1/files/0/Movie%20(2020)/Movie%20(2020).mp4", // the film
      "/v1/files/0/Movie%20(2020)/Trailers/Trailer.mp4", // an extra
      "/v1/files/0/Movie%20(2020)/menus/secret.png", // exists, but no manifest names it
      "/v1/files/0/secrets.txt",
      "/v1/files/0/Movie%20(2020)/../secrets.txt",
      "/v1/files/0/Movie%20(2020)/%2e%2e/secrets.txt",
      "/v1/files/0/..%2fsecrets.txt",
      "/v1/files/1/Movie%20(2020)/menus/bg.jpg", // no such root
      "/v1/files/0/Broken/Broken.menustream.json/../../secrets.txt",
    ])
      expect((await get(path)).status, path).toBe(404);
  });

  it("rejects traversal sent raw (fetch would normalize ../ away before sending)", async () => {
    const { request } = await import("node:http");
    const raw = (path: string) =>
      new Promise<number>((resolve, reject) => {
        const req = request(base + "/", { path, headers: { "X-Plex-Token": "good" } }, (res) => {
          res.resume();
          resolve(res.statusCode!);
        });
        req.on("error", reject);
        req.end();
      });
    expect(await raw("/v1/files/0/Movie%20(2020)/../secrets.txt")).toBe(404);
    expect(await raw("/v1/files/0/Movie%20(2020)/menus/../../secrets.txt")).toBe(404);
    expect(await raw("/v1/files/0/../../../../etc/passwd")).toBe(404);
    expect(await raw("/v1/files/0/Movie%20(2020)/menus/..%5c..%5csecrets.txt")).toBe(404);
    expect(await raw("/v1/files/0/Movie%20(2020)/menus/bg.jpg")).toBe(200);
  });

  it("is read-only", async () => {
    const res = await fetch(base + "/v1/index", { method: "POST", headers: { "X-Plex-Token": "good" } });
    expect(res.status).toBe(405);
  });
});

describe("PlexTokenCheck", () => {
  const plexFetch = () =>
    vi.fn(async (url: string | URL, init?: RequestInit) => {
      const u = String(url);
      const token = (init?.headers as Record<string, string>)["X-Plex-Token"];
      if (u.endsWith("/identity")) return Response.json({ MediaContainer: { machineIdentifier: "server-1" } });
      if (u.startsWith("https://plex.tv/api/v2/resources")) {
        if (token === "owner") return Response.json([{ clientIdentifier: "server-1", provides: "server" }]);
        if (token === "other") return Response.json([{ clientIdentifier: "someone-elses-server", provides: "server" }]);
        return new Response("", { status: 401 });
      }
      return new Response("", { status: 404 });
    });

  it("allows tokens that can access this server and refuses others", async () => {
    const f = plexFetch();
    const check = new PlexTokenCheck("http://plex.local:32400", f as unknown as typeof fetch);
    expect(await check.allows("owner")).toBe(true);
    expect(await check.allows("other")).toBe(false);
    expect(await check.allows("garbage")).toBe(false);
    expect(await check.allows(undefined)).toBe(false);
  });

  it("caches results and never sends the token to the Plex server itself", async () => {
    let now = 0;
    const f = plexFetch();
    const check = new PlexTokenCheck("http://plex.local:32400", f as unknown as typeof fetch, () => now);
    await check.allows("owner");
    await check.allows("owner");
    const resourceCalls = () => f.mock.calls.filter(([u]) => String(u).includes("plex.tv")).length;
    expect(resourceCalls()).toBe(1);
    now += 11 * 60_000;
    await check.allows("owner");
    expect(resourceCalls()).toBe(2);
    const identityCall = f.mock.calls.find(([u]) => String(u).endsWith("/identity"))!;
    expect((identityCall[1]!.headers as Record<string, string>)["X-Plex-Token"]).toBeUndefined();
  });

  it("refuses when plex.tv is unreachable", async () => {
    const f = vi.fn(async (url: string | URL) => {
      if (String(url).endsWith("/identity")) return Response.json({ MediaContainer: { machineIdentifier: "server-1" } });
      throw new Error("offline");
    });
    expect(await new PlexTokenCheck("http://plex.local:32400", f as unknown as typeof fetch).allows("owner")).toBe(false);
  });
});

describe("isLocalAddress", () => {
  it.each([
    ["127.0.0.1", true],
    ["::1", true],
    ["192.168.1.40", true],
    ["10.0.0.5", true],
    ["172.16.0.1", true],
    ["172.31.255.255", true],
    ["100.101.102.103", true], // Tailscale
    ["::ffff:192.168.1.40", true],
    ["fd12:3456::1", true],
    ["fe80::1", true],
    ["172.32.0.1", false],
    ["8.8.8.8", false],
    ["::ffff:8.8.8.8", false],
    ["2001:4860::8888", false],
    [undefined, false],
  ])("%s → %s", (ip, expected) => expect(isLocalAddress(ip)).toBe(expected));
});

describe("NoAuth", () => {
  it("allows everything (testing only)", async () => expect(await new NoAuth().allows(undefined)).toBe(true));
});
