import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterAll, describe, expect, it, vi } from "vitest";
import { Catalog, ItemIndex, JellyfinLibrary, JellyfinLive, LibraryFollower, LiveUpdates, PlexLibrary, PlexLive, PlexTokenCheck, type LibrarySource, type SocketLike } from "../src/index.js";

/** A fetch that answers URL paths (with their query) by prefix, with a JSON body or a status. */
function fakeFetch(answers: Record<string, unknown>): typeof fetch {
  return (async (input: string | URL | Request) => {
    const url = new URL(String(input));
    const key = Object.keys(answers).find((k) => (url.pathname + url.search).startsWith(k));
    if (!key) return new Response("no", { status: 404 });
    const body = answers[key];
    return typeof body === "number" ? new Response("", { status: body }) : Response.json(body);
  }) as typeof fetch;
}

describe("library items: which item plays a file", () => {
  it("matches by trailing path segments across different roots, case-insensitively", () => {
    const index = new ItemIndex();
    index.add("plex-1", [
      { id: "7995", type: "movie", files: ["K:\\MP4\\Movies\\The Matrix (1999)\\The Matrix (1999).mp4"] },
      { id: "12", type: "episode", show: "10", files: ["K:\\MP4\\TV\\Show\\Season 01\\Show - s01e01.mkv"] },
    ]);
    expect(index.find("//mediacenter/MP4/Movies/The Matrix (1999)/the matrix (1999).mp4")).toEqual([{ server: "plex-1", id: "7995", type: "movie" }]);
    expect(index.find("/mnt/tv/Show/Season 01/Show - s01e01.mkv")).toEqual([{ server: "plex-1", id: "12", type: "episode", show: "10" }]);
  });

  it("needs the file name and its folder, and a tie between two items is no match", () => {
    const index = new ItemIndex();
    index.add("s", [
      { id: "1", type: "movie", files: ["/a/Film A/Trailer.mp4"] },
      { id: "2", type: "movie", files: ["/b/Film B/Trailer.mp4"] },
      { id: "3", type: "movie", files: ["/x/One/Extras/Clip.mp4"] },
      { id: "4", type: "movie", files: ["/y/Two/Extras/Clip.mp4"] },
    ]);
    expect(index.find("/m/Film A/Trailer.mp4")).toEqual([{ server: "s", id: "1", type: "movie" }]);
    expect(index.find("/m/Other/Trailer.mp4")).toEqual([]);
    expect(index.find("/m/Extras/Clip.mp4")).toEqual([]); // "Extras/Clip.mp4" fits both equally
    expect(index.find("/m/Two/Extras/Clip.mp4")).toEqual([{ server: "s", id: "4", type: "movie" }]);
  });

  it("Plex: films of movie sections and episodes of show sections, and the machine id", async () => {
    const plex = new PlexLibrary(
      "http://plex:32400",
      "t",
      fakeFetch({
        "/identity": { MediaContainer: { machineIdentifier: "m-1" } },
        "/library/sections/1/all?type=1": { MediaContainer: { Metadata: [{ ratingKey: "5", Media: [{ Part: [{ file: "/m/F/F.mp4" }] }] }] } },
        "/library/sections/2/all?type=4": {
          MediaContainer: { Metadata: [{ ratingKey: "9", grandparentRatingKey: "8", Media: [{ Part: [{ file: "/t/S/e1.mkv" }, { file: "/t/S/e1b.mkv" }] }] }] },
        },
        "/library/sections": { MediaContainer: { Directory: [{ key: "1", type: "movie" }, { key: "2", type: "show" }, { key: "3", type: "artist" }] } },
      }),
    );
    expect(await plex.serverId()).toBe("m-1");
    expect(await plex.items()).toEqual([
      { id: "5", type: "movie", files: ["/m/F/F.mp4"] },
      { id: "9", type: "episode", show: "8", files: ["/t/S/e1.mkv", "/t/S/e1b.mkv"] },
    ]);
  });

  it("Jellyfin: films and episodes with their media sources' paths, and the server id", async () => {
    const jf = new JellyfinLibrary(
      "http://jf:8096",
      "k",
      fakeFetch({
        "/System/Info/Public": { Id: "jf-1" },
        "/Items": {
          Items: [
            { Id: "a", Type: "Movie", Path: "/m/F/F.mkv", MediaSources: [{ Path: "/m/F/F.mkv" }, { Path: "/m/F/F - 4K.mkv" }] },
            { Id: "b", Type: "Episode", SeriesId: "s", Path: "/t/S/e1.mkv" },
            { Id: "c", Type: "Series", Path: "/t/S" },
          ],
        },
      }),
    );
    expect(await jf.serverId()).toBe("jf-1");
    expect(await jf.items()).toEqual([
      { id: "a", type: "movie", files: ["/m/F/F.mkv", "/m/F/F - 4K.mkv"] },
      { id: "b", type: "episode", show: "s", files: ["/t/S/e1.mkv"] },
    ]);
  });
});

describe("the index says which library item each media is", () => {
  const dir = mkdtempSync(join(tmpdir(), "ovm-items-"));
  afterAll(() => rmSync(dir, { recursive: true, force: true }));
  mkdirSync(join(dir, "Film (2000)"), { recursive: true });
  writeFileSync(
    join(dir, "Film (2000)", "Film (2000).openvideomenu.json"),
    JSON.stringify({
      format: "openvideomenu",
      version: "0.1",
      title: { name: "Film" },
      media: { feature: { ref: "Film (2000).mp4" }, trailer: { ref: "Trailers/Trailer.mp4" }, web: { ref: "https://example.com/x.m3u8" } },
      menus: { main: { canvas: { width: 1280, height: 720 }, buttons: [{ id: "p", rect: [0, 0, 10, 10], action: { type: "play", media: "feature" } }] } },
      rootMenu: "main",
      fallback: [{ label: "Film", items: [{ label: "Play", action: { type: "play", media: "feature" } }] }],
    }),
  );

  const source = (items: () => Promise<{ id: string; type: "movie" | "episode"; files: string[] }[]>): LibrarySource => ({
    name: "Plex",
    kind: "plex",
    folders: async () => ["/plex/Films"],
    serverId: async () => "m-1",
    items,
  });

  it("lists the item for each media whose file the media server has, and keeps it when the server is away", async () => {
    let up = true;
    const catalog = new Catalog([dir]);
    const follower = new LibraryFollower(
      [
        source(async () => {
          if (!up) throw new Error("down");
          return [{ id: "42", type: "movie", files: ["/plex/Films/Film (2000)/Film (2000).mp4"] }];
        }),
      ],
      [dir],
      [],
      catalog,
      () => {},
    );
    await follower.refresh(true);
    await catalog.scan();
    const entry = () => JSON.parse(catalog.index({}).json).manifests[0];
    expect(entry().items).toEqual([{ media: "feature", server: "m-1", id: "42", type: "movie" }]);
    up = false;
    await follower.refresh(true);
    expect(entry().items).toEqual([{ media: "feature", server: "m-1", id: "42", type: "movie" }]);
  });

  it("has no items field without a media server's items", async () => {
    const catalog = new Catalog([dir]);
    await catalog.scan();
    expect(JSON.parse(catalog.index({}).json).manifests[0].items).toBeUndefined();
  });
});

/** A socket the test drives: it opens when told, and records what was sent. */
class FakeSocket implements SocketLike {
  onopen: ((ev: unknown) => void) | null = null;
  onmessage: ((ev: { data: unknown }) => void) | null = null;
  onclose: ((ev: unknown) => void) | null = null;
  onerror: ((ev: unknown) => void) | null = null;
  sent: string[] = [];
  constructor(readonly url: string) {}
  send(d: string) {
    this.sent.push(d);
  }
  close() {
    this.onclose?.({});
  }
  message(body: unknown) {
    this.onmessage?.({ data: JSON.stringify(body) });
  }
}

describe("library changes as they happen", () => {
  it("Plex: a timeline entry or an ended library activity is a change; the token rides in the URL", () => {
    const p = new PlexLive("http://plex:32400", "secret");
    expect(p.url).toBe("ws://plex:32400/:/websockets/notifications?X-Plex-Token=secret");
    expect(p.isChange({ NotificationContainer: { type: "timeline", TimelineEntry: [{ state: 5 }] } })).toBe(true);
    expect(p.isChange({ NotificationContainer: { type: "activity", ActivityNotification: [{ event: "ended", Activity: { type: "library.update.section" } }] } })).toBe(true);
    expect(p.isChange({ NotificationContainer: { type: "activity", ActivityNotification: [{ event: "started", Activity: { type: "library.update.section" } }] } })).toBe(false);
    expect(p.isChange({ NotificationContainer: { type: "playing", PlaySessionStateNotification: [{}] } })).toBe(false);
  });

  it("calls back once after a burst of changes, reports connection, and reconnects", async () => {
    vi.useFakeTimers();
    try {
      const sockets: FakeSocket[] = [];
      const onChange = vi.fn();
      const connected: boolean[] = [];
      const live = new LiveUpdates([new PlexLive("http://plex:32400", "t")], {
        onChange,
        onConnected: (c) => connected.push(c),
        makeSocket: (url) => {
          const s = new FakeSocket(url);
          sockets.push(s);
          return s;
        },
        settleMs: 1000,
      });
      expect(live.start()).toBe(true);
      sockets[0]!.onopen?.({});
      for (let i = 0; i < 5; i++) sockets[0]!.message({ NotificationContainer: { type: "timeline", TimelineEntry: [{}] } });
      vi.advanceTimersByTime(999);
      expect(onChange).not.toHaveBeenCalled();
      vi.advanceTimersByTime(1);
      expect(onChange).toHaveBeenCalledTimes(1);
      sockets[0]!.close();
      expect(connected).toEqual([true, false]);
      vi.advanceTimersByTime(5000);
      expect(sockets).toHaveLength(2);
      live.stop();
    } finally {
      vi.useRealTimers();
    }
  });

  it("Jellyfin: LibraryChanged is a change, and it answers the server's ForceKeepAlive", () => {
    vi.useFakeTimers();
    try {
      const j = new JellyfinLive("https://jf.example:8920", "key");
      expect(j.url).toBe("wss://jf.example:8920/socket?api_key=key&deviceId=openvideomenu-menu-server");
      expect(j.isChange({ MessageType: "LibraryChanged", Data: {} })).toBe(true);
      expect(j.isChange({ MessageType: "UserDataChanged" })).toBe(false);
      const sent: string[] = [];
      j.onMessage({ MessageType: "ForceKeepAlive", Data: 60 }, (d) => sent.push(d));
      expect(sent).toEqual(['{"MessageType":"KeepAlive"}']);
      vi.advanceTimersByTime(30_000);
      expect(sent).toHaveLength(2);
      j.stop();
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("Plex sign-ins checked by the Plex server itself", () => {
  /** A Plex server (and plex.tv) for the check: which tokens it accepts, and whether it lets anyone in. */
  const setup = (o: { openLan?: boolean; down?: boolean }) =>
    vi.fn(async (url: string | URL, init?: RequestInit) => {
      const u = String(url);
      const token = (init?.headers as Record<string, string>)["X-Plex-Token"];
      if (u.startsWith("https://plex.tv/api/v2/resources")) return token === "owner" ? Response.json([{ clientIdentifier: "m-1" }]) : new Response("", { status: 401 });
      if (o.down) throw new Error("connect ECONNREFUSED");
      if (u.endsWith("/identity")) return Response.json({ MediaContainer: { machineIdentifier: "m-1" } });
      if (u.endsWith("/library/sections")) return new Response("", { status: o.openLan || token === "owner" || token === "shared" ? 200 : 401 });
      return new Response("", { status: 404 });
    });
  const plex = (token: string) => ({ kind: "plex" as const, token });
  const plexTvCalls = (f: ReturnType<typeof setup>) => f.mock.calls.filter(([u]) => String(u).startsWith("https://plex.tv")).length;

  it("accepts what the server accepts and refuses the rest, without plex.tv", async () => {
    const f = setup({});
    const check = new PlexTokenCheck("http://plex:32400", f as unknown as typeof fetch);
    expect(await check.allows(plex("shared"))).toBe(true);
    expect(await check.allows(plex("stranger"))).toBe(false);
    expect(plexTvCalls(f)).toBe(0);
  });

  it("asks plex.tv when the server lets the home network in without signing in", async () => {
    const f = setup({ openLan: true });
    const check = new PlexTokenCheck("http://plex:32400", f as unknown as typeof fetch);
    expect(await check.allows(plex("stranger"))).toBe(false);
    expect(await check.allows(plex("owner"))).toBe(true);
    expect(plexTvCalls(f)).toBe(2);
  });

  it("asks plex.tv when the server can't be reached", async () => {
    const f = setup({ down: true });
    const check = new PlexTokenCheck("http://plex:32400", f as unknown as typeof fetch);
    expect(await check.allows(plex("owner"))).toBe(false); // no machine id to compare without the server
    expect(plexTvCalls(f)).toBe(0);
  });
});
