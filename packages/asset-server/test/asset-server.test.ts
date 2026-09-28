import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { afterAll, beforeAll, describe, expect, it, vi } from "vitest";
import { AnyOf, Catalog, createAssetServer, isLocalAddress, JellyfinTokenCheck, NoAuth, PlexTokenCheck, type Credential, type TokenCheck } from "../src/index.js";
import { legacyUnicastReply } from "../src/discovery.js";

const plex = (token: string): Credential => ({ kind: "plex", token });
const jellyfin = (token: string): Credential => ({ kind: "jellyfin", token });

const MANIFEST = {
  format: "openvideomenu",
  version: "0.1",
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
  write("Movie (2020)/Movie (2020).openvideomenu.json", JSON.stringify(MANIFEST));
  write("Movie (2020)/Movie (2020).mp4", "FILM");
  write("Movie (2020)/Trailers/Trailer.mp4", "TRAILER");
  write("Movie (2020)/menus/bg.jpg", "JPG");
  write("Movie (2020)/menus/loop.mp4", Buffer.alloc(1000, 7));
  write("Movie (2020)/menus/sel.png", "PNG");
  write("Movie (2020)/menus/whoosh.mp4", "WHOOSH");
  write("Movie (2020)/menus/secret.png", "NOT REFERENCED");
  write("Broken/Broken.openvideomenu.json", "{ nope");
  write("secrets.txt", "top secret");
});
afterAll(() => rmSync(root, { recursive: true, force: true }));

describe("Catalog", () => {
  it("indexes manifests with root-relative media paths and menu assets", async () => {
    const catalog = new Catalog([root]);
    await catalog.scan();
    const entries = catalog.entries();
    expect(entries.map((e) => [e.path, e.valid])).toEqual([
      ["Broken/Broken.openvideomenu.json", false],
      ["Movie (2020)/Movie (2020).openvideomenu.json", true],
    ]);
    const movie = entries[1]!;
    expect(movie.title?.name).toBe("Movie");
    expect(movie.hasMenus).toBe(true);
    expect(movie.url).toBe("/v1/files/0/Movie%20(2020)/Movie%20(2020).openvideomenu.json");
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
  it("serves files under roots given with a trailing separator (UNC shares and drive roots resolve that way)", async () => {
    const withSlash = new Catalog([root + "/"]);
    await withSlash.scan();
    expect(withSlash.resolveFile(0, "Movie (2020)/menus/bg.jpg")).toBeDefined();
    expect(withSlash.resolveFile(0, "Movie (2020)/Movie (2020).mp4")).toBeUndefined();
    expect(withSlash.resolveFile(0, "../secrets.txt")).toBeUndefined();
  });

  it.runIf(process.platform === "win32")("serves files under a UNC-style root", async () => {
    // \\localhost\X$\... is a UNC path to the same folder; it resolves with a trailing separator.
    const drive = root.slice(0, 1);
    const unc = new Catalog([`\\\\localhost\\${drive}$${root.slice(2)}`]);
    await unc.scan(); // an unreachable root (admin shares disabled) just yields no manifests
    if (unc.entries().length === 0) return;
    expect(unc.resolveFile(0, "Movie (2020)/menus/bg.jpg")).toBeDefined();
  });
});

describe("server", () => {
  let base: string;
  let close: () => void;
  const allow: TokenCheck = { description: "test", allows: async (c) => c?.token === "good" };

  beforeAll(async () => {
    const server = createAssetServer({ catalog: new Catalog([root]), auth: allow });
    await new Promise<void>((r) => server.listen(0, "127.0.0.1", r));
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
    close = () => server.close();
  });
  afterAll(() => close());

  const get = (path: string, headers: Record<string, string> = { "X-Plex-Token": "good" }) => fetch(base + path, { headers });

  it("answers health without a token, without waiting for the first scan", async () => {
    const res = await get("/v1/health", {});
    expect(res.status).toBe(200);
    const health = (await res.json()) as { manifests: number; scanning: boolean };
    // The first scan has at most just begun; the index waits for it.
    expect(health.scanning).toBe(true);
    await get("/v1/index");
    const after = (await (await get("/v1/health", {})).json()) as { manifests: number; scanning: boolean };
    expect(after).toMatchObject({ manifests: 2, scanning: false });
  });

  it("sends a slim index: no asset lists or warnings, gzipped on request, 304 when unchanged", async () => {
    const res = await get("/v1/index");
    const index = (await res.json()) as { manifests: Record<string, unknown>[] };
    for (const m of index.manifests) {
      expect(m).not.toHaveProperty("assets");
      expect(m).not.toHaveProperty("warnings");
    }
    const etag = res.headers.get("etag");
    expect(etag).toMatch(/^"[0-9a-f]+"$/);
    expect(res.headers.get("cache-control")).toBe("private, no-cache");

    const gz = await get("/v1/index", { "X-Plex-Token": "good", "Accept-Encoding": "gzip" });
    expect(gz.headers.get("content-encoding")).toBe("gzip");
    expect(((await gz.json()) as { manifests: unknown[] }).manifests).toHaveLength(2); // fetch un-gzips

    const same = await get("/v1/index", { "X-Plex-Token": "good", "If-None-Match": etag! });
    expect(same.status).toBe(304);
    expect(await same.text()).toBe("");
  });

  it("requires a valid token for the index and files", async () => {
    expect((await get("/v1/index", {})).status).toBe(401);
    expect((await get("/v1/index", { "X-Plex-Token": "bad" })).status).toBe(401);
    expect((await get("/v1/index", { Authorization: "Bearer good" })).status).toBe(200);
    // Jellyfin clients send their own Authorization header; the token is read from it.
    const jf = (token: string) => ({ Authorization: `MediaBrowser Client="ovmplayer", Device="TV", DeviceId="d1", Version="1", Token="${token}"` });
    expect((await get("/v1/index", jf("good"))).status).toBe(200);
    expect((await get("/v1/index", jf("bad"))).status).toBe(401);
    expect((await get("/v1/index", { Authorization: 'MediaBrowser Client="ovmplayer", DeviceId="d1"' })).status).toBe(401);
    const index = (await (await get("/v1/index")).json()) as { manifests: unknown[] };
    expect(index.manifests).toHaveLength(2);
  });

  it("serves manifests and the menu art they name", async () => {
    const manifest = await get("/v1/files/0/Movie%20(2020)/Movie%20(2020).openvideomenu.json");
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
      "/v1/files/0/Broken/Broken.openvideomenu.json/../../secrets.txt",
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
    expect(await check.allows(plex("owner"))).toBe(true);
    expect(await check.allows(plex("other"))).toBe(false);
    expect(await check.allows(plex("garbage"))).toBe(false);
    expect(await check.allows(undefined)).toBe(false);
    expect(await check.allows(jellyfin("owner"))).toBe(false); // a Jellyfin sign-in is not a Plex one
  });

  it("caches results and never sends the token to the Plex server itself", async () => {
    let now = 0;
    const f = plexFetch();
    const check = new PlexTokenCheck("http://plex.local:32400", f as unknown as typeof fetch, () => now);
    await check.allows(plex("owner"));
    await check.allows(plex("owner"));
    const resourceCalls = () => f.mock.calls.filter(([u]) => String(u).includes("plex.tv")).length;
    expect(resourceCalls()).toBe(1);
    now += 11 * 60_000;
    await check.allows(plex("owner"));
    expect(resourceCalls()).toBe(2);
    const identityCall = f.mock.calls.find(([u]) => String(u).endsWith("/identity"))!;
    expect((identityCall[1]!.headers as Record<string, string>)["X-Plex-Token"]).toBeUndefined();
  });

  it("refuses when plex.tv is unreachable", async () => {
    const f = vi.fn(async (url: string | URL) => {
      if (String(url).endsWith("/identity")) return Response.json({ MediaContainer: { machineIdentifier: "server-1" } });
      throw new Error("offline");
    });
    expect(await new PlexTokenCheck("http://plex.local:32400", f as unknown as typeof fetch).allows(plex("owner"))).toBe(false);
  });
});

describe("JellyfinTokenCheck", () => {
  const jellyfinFetch = () =>
    vi.fn(async (url: string | URL, init?: RequestInit) => {
      const auth = (init?.headers as Record<string, string>).Authorization ?? "";
      if (!String(url).endsWith("/Users/Me")) return new Response("", { status: 404 });
      if (!auth.startsWith("MediaBrowser ")) return new Response("", { status: 401 });
      if (auth.includes('Token="user-token"')) return Response.json({ Id: "user-1", Name: "tyson" });
      return new Response("", { status: 401 });
    });

  it("allows tokens the Jellyfin server accepts and refuses others", async () => {
    const f = jellyfinFetch();
    const check = new JellyfinTokenCheck("http://jellyfin.local:8096", f as unknown as typeof fetch);
    expect(await check.allows(jellyfin("user-token"))).toBe(true);
    expect(await check.allows(jellyfin("revoked"))).toBe(false);
    expect(await check.allows(plex("user-token"))).toBe(false); // a Plex sign-in is not a Jellyfin one
    expect(await check.allows(undefined)).toBe(false);
  });

  it("caches results by token", async () => {
    let now = 0;
    const f = jellyfinFetch();
    const check = new JellyfinTokenCheck("http://jellyfin.local:8096", f as unknown as typeof fetch, () => now);
    await check.allows(jellyfin("user-token"));
    await check.allows(jellyfin("user-token"));
    expect(f).toHaveBeenCalledTimes(1);
    now += 11 * 60_000;
    await check.allows(jellyfin("user-token"));
    expect(f).toHaveBeenCalledTimes(2);
  });

  it("refuses when the Jellyfin server is unreachable", async () => {
    const f = vi.fn(async () => {
      throw new Error("offline");
    });
    expect(await new JellyfinTokenCheck("http://jellyfin.local:8096", f as unknown as typeof fetch).allows(jellyfin("user-token"))).toBe(false);
  });
});

describe("AnyOf", () => {
  it("accepts a sign-in any configured server accepts", async () => {
    const plexOnly: TokenCheck = { description: "plex", allows: async (c) => c?.kind === "plex" && c.token === "p" };
    const jfOnly: TokenCheck = { description: "jellyfin", allows: async (c) => c?.kind === "jellyfin" && c.token === "j" };
    const both = new AnyOf([plexOnly, jfOnly]);
    expect(both.description).toBe("plex or jellyfin");
    expect(await both.allows(plex("p"))).toBe(true);
    expect(await both.allows(jellyfin("j"))).toBe(true);
    expect(await both.allows(jellyfin("p"))).toBe(false);
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

describe("isFaststart / checkAssetFiles", () => {
  const box = (type: string, payload = 8) => {
    const b = Buffer.alloc(8 + payload);
    b.writeUInt32BE(8 + payload, 0);
    b.write(type, 4, "latin1");
    return b;
  };
  const largeMdat = () => {
    const b = Buffer.alloc(24);
    b.writeUInt32BE(1, 0); // size 1 → 64-bit size follows
    b.write("mdat", 4, "latin1");
    b.writeBigUInt64BE(24n, 8);
    return b;
  };

  it("detects where the index is", async () => {
    const { isFaststart, checkAssetFiles } = await import("../src/index.js");
    write("mp4/fast.m4a", Buffer.concat([box("ftyp"), box("moov"), box("mdat", 100)]));
    write("mp4/slow.m4a", Buffer.concat([box("ftyp"), box("mdat", 100), box("moov")]));
    write("mp4/slow64.mp4", Buffer.concat([box("ftyp"), largeMdat(), box("moov")]));
    write("mp4/skip.mp4", Buffer.concat([box("ftyp"), box("free", 40), box("moov"), box("mdat")]));
    const f = (n: string) => join(root, "mp4", n);
    expect(await isFaststart(f("fast.m4a"))).toBe(true);
    expect(await isFaststart(f("slow.m4a"))).toBe(false);
    expect(await isFaststart(f("slow64.mp4"))).toBe(false);
    expect(await isFaststart(f("skip.mp4"))).toBe(true);
    expect(await isFaststart(join(root, "Movie (2020)/menus/bg.jpg"))).toBeUndefined();

    const problems = await checkAssetFiles([
      { shown: "fast.m4a", file: f("fast.m4a") },
      { shown: "slow.m4a", file: f("slow.m4a") },
      { shown: "missing.png", file: f("missing.png") },
    ]);
    expect(problems).toHaveLength(2);
    expect(problems[0]).toMatch(/slow\.m4a has its index at the end.*faststart/);
    expect(problems[1]).toBe("Menu asset not found: missing.png");
  });
});

describe("checkIgnoreFiles", () => {
  it("warns once per folder with menu motion/music but no ignore files", async () => {
    const { checkIgnoreFiles } = await import("../src/index.js");
    write("ig/bare/loop.mp4");
    write("ig/bare/music.m4a");
    write("ig/bare/still.png");
    write("ig/done/loop.mp4");
    write("ig/done/.plexignore", "*\n");
    write("ig/done/.ignore", "");
    write("ig/stills/only.png");
    const a = (p: string) => ({ shown: p, file: join(root, ...p.split("/")) });
    const problems = await checkIgnoreFiles([a("ig/bare/loop.mp4"), a("ig/bare/music.m4a"), a("ig/bare/still.png"), a("ig/done/loop.mp4"), a("ig/stills/only.png")]);
    expect(problems).toHaveLength(1);
    expect(problems[0]).toMatch(/^ig[\\/]bare holds menu video\/music but no \.plexignore or \.ignore/);
  });
});

describe("NoAuth", () => {
  it("allows everything (testing only)", async () => expect(await new NoAuth().allows(undefined)).toBe(true));
});

describe("legacyUnicastReply", () => {
  const records = [
    { name: "_services._dns-sd._udp.local", type: "PTR", ttl: 120, data: "_openvideomenu._tcp.local" },
    { name: "_openvideomenu._tcp.local", type: "PTR", ttl: 28800, data: "Den._openvideomenu._tcp.local" },
    { name: "Den._openvideomenu._tcp.local", type: "SRV", ttl: 120, data: { port: 8790, target: "pc.local" } },
    { name: "Den._openvideomenu._tcp.local", type: "TXT", ttl: 4500, data: ["api=1"] },
    { name: "pc.local", type: "A", ttl: 120, flush: true, data: "192.168.1.20" },
  ];

  it("answers a PTR browse with the service's SRV, TXT and address, echoing ID and question", () => {
    const query = { id: 4242, questions: [{ name: "_openvideomenu._tcp.local", type: "PTR" }] };
    const reply = legacyUnicastReply(query, records)!;
    expect(reply.id).toBe(4242);
    expect(reply.questions).toEqual(query.questions);
    expect(reply.answers.map((r) => r.type)).toEqual(["PTR"]);
    expect(reply.additionals.map((r) => r.type).sort()).toEqual(["A", "SRV", "TXT"]);
  });

  it("caps TTLs at 10 s and clears the cache-flush bit", () => {
    const reply = legacyUnicastReply({ questions: [{ name: "_OPENVIDEOMENU._tcp.local", type: "PTR" }] }, records)!;
    for (const r of [...reply.answers, ...reply.additionals]) {
      expect(r.ttl).toBeLessThanOrEqual(10);
      expect(r.flush).toBe(false);
    }
  });

  it("stays quiet for questions it can't answer", () => {
    expect(legacyUnicastReply({ questions: [{ name: "_googlecast._tcp.local", type: "PTR" }] }, records)).toBeUndefined();
  });
});
