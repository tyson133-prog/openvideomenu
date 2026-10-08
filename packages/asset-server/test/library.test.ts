import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { afterAll, describe, expect, it } from "vitest";
import { applyPathMap, Catalog, JellyfinLibrary, LibraryFollower, parseEnvFile, parsePathMap, PlexLibrary, type LibrarySource } from "../src/index.js";

/** A fetch that answers one URL path with a JSON body (or a status), and records the headers it got. */
function fakeFetch(answers: Record<string, unknown>, seen: Headers[] = []): typeof fetch {
  return (async (input: string | URL | Request, init?: RequestInit) => {
    const url = new URL(String(input));
    seen.push(new Headers(init?.headers));
    if (!(url.pathname in answers)) return new Response("no", { status: 404 });
    const body = answers[url.pathname];
    return typeof body === "number" ? new Response("", { status: body }) : Response.json(body);
  }) as typeof fetch;
}

describe("library folders from the media server", () => {
  it("Plex: movie and TV sections' locations, with the token in a header", async () => {
    const seen: Headers[] = [];
    const plex = new PlexLibrary(
      "http://plex:32400",
      "secret",
      fakeFetch(
        {
          "/library/sections": {
            MediaContainer: {
              Directory: [
                { type: "movie", Location: [{ path: "D:\\Movies" }, { path: "\\\\nas\\Films" }] },
                { type: "show", Location: [{ path: "/data/tv" }] },
                { type: "artist", Location: [{ path: "/data/music" }] },
                { type: "photo", Location: [{ path: "/data/photos" }] },
              ],
            },
          },
        },
        seen,
      ),
    );
    expect(await plex.folders()).toEqual(["D:\\Movies", "\\\\nas\\Films", "/data/tv"]);
    expect(seen[0]!.get("X-Plex-Token")).toBe("secret");
    expect(seen[0]!.get("Accept")).toBe("application/json");
  });

  it("Jellyfin: movie, TV, mixed and home-video libraries, with the API key", async () => {
    const seen: Headers[] = [];
    const jf = new JellyfinLibrary(
      "http://jf:8096",
      "key1",
      fakeFetch(
        {
          "/Library/VirtualFolders": [
            { CollectionType: "movies", Locations: ["/media/movies"] },
            { CollectionType: "tvshows", Locations: ["/media/tv"] },
            { CollectionType: null, Locations: ["/media/mixed"] },
            { CollectionType: "homevideos", Locations: ["/media/home"] },
            { CollectionType: "music", Locations: ["/media/music"] },
            { CollectionType: "books", Locations: ["/media/books"] },
          ],
        },
        seen,
      ),
    );
    expect(await jf.folders()).toEqual(["/media/movies", "/media/tv", "/media/mixed", "/media/home"]);
    expect(seen[0]!.get("Authorization")).toBe('MediaBrowser Token="key1"');
  });

  it("an error answer throws", async () => {
    await expect(new PlexLibrary("http://plex:32400", "bad", fakeFetch({ "/library/sections": 401 })).folders()).rejects.toThrow("401");
  });
});

describe("path maps", () => {
  it("parses from=to pairs and rejects bad ones", () => {
    expect(parsePathMap(["/data=/mnt/media", "Z:\\=\\\\nas\\share"])).toEqual([
      { from: "/data", to: "/mnt/media" },
      { from: "Z:\\", to: "\\\\nas\\share" },
    ]);
    expect(() => parsePathMap(["/data"])).toThrow("<from>=<to>");
    expect(() => parsePathMap(["=/x"])).toThrow();
  });

  it("replaces the longest prefix at a folder boundary", () => {
    const map = parsePathMap(["/data=/mnt/media", "/data/tv=/srv/tv"]);
    expect(applyPathMap("/data/movies/Heat (1995)", map)).toBe("/mnt/media/movies/Heat (1995)");
    expect(applyPathMap("/data/tv", map)).toBe("/srv/tv");
    expect(applyPathMap("/data/tv/Show", map)).toBe("/srv/tv/Show");
    expect(applyPathMap("/database/x", map)).toBe("/database/x"); // not a folder boundary
    expect(applyPathMap("/other", map)).toBe("/other");
  });

  it("Windows paths: case and separators don't matter; the result uses the target's separator", () => {
    const map = parsePathMap(["Z:\\=\\\\nas\\media", "/data=D:\\Media"]);
    expect(applyPathMap("z:/Movies/Heat", map)).toBe("\\\\nas\\media\\Movies\\Heat");
    expect(applyPathMap("Z:\\Movies", map)).toBe("\\\\nas\\media\\Movies");
    expect(applyPathMap("/data/tv/Show", map)).toBe("D:\\Media\\tv\\Show");
  });
});

describe("settings file", () => {
  it("reads KEY=VALUE lines with comments, blanks, quotes and export", () => {
    expect(
      parseEnvFile(
        ["# Open Video Menu menu server", "", "PLEX_URL=http://127.0.0.1:32400", 'PLEX_TOKEN="abc def"', "export PORT='8790'", "  MEDIA_ROOTS = D:\\Movies,E:\\TV  ", "not a setting"].join("\r\n"),
      ),
    ).toEqual({ PLEX_URL: "http://127.0.0.1:32400", PLEX_TOKEN: "abc def", PORT: "8790", MEDIA_ROOTS: "D:\\Movies,E:\\TV" });
  });
});

describe("following the libraries", () => {
  const dir = mkdtempSync(join(tmpdir(), "ovm-library-"));
  const folder = (name: string) => {
    mkdirSync(join(dir, name), { recursive: true });
    writeFileSync(join(dir, name, "Film.openvideomenu.json"), JSON.stringify({ format: "openvideomenu", version: "0.1", media: { f: { ref: "Film.mp4" } }, fallback: [] }));
    return join(dir, name);
  };
  afterAll(() => rmSync(dir, { recursive: true, force: true }));

  /** A source whose folders (or failure) the test sets. */
  const source = (name: string) => {
    const s = { name, list: [] as string[], fail: false, async folders() {
      if (s.fail) throw new Error("connect ECONNREFUSED");
      return s.list;
    } };
    return s satisfies LibrarySource;
  };

  it("uses the library's folders (mapped) plus the extra ones, follows changes, and keeps the last list when unreachable", async () => {
    const a = folder("a");
    const b = folder("b");
    const extra = folder("extra");
    const plex = source("Plex");
    plex.list = ["/plex/a"];
    const catalog = new Catalog([extra]);
    const log: string[] = [];
    const follower = new LibraryFollower([plex], [extra], parsePathMap([`/plex=${dir}`]), catalog, (l) => log.push(l));

    await follower.refresh();
    expect(catalog.setRoots([a, extra])).toBe(false); // already the pending roots
    await catalog.scan();
    expect(catalog.roots).toEqual([resolve(a), resolve(extra)]);
    expect(catalog.entries().map((e) => e.root)).toEqual([0, 1]);
    expect(log.some((l) => l.startsWith("Library folders:"))).toBe(true);

    // A new library folder appears; the old one goes.
    plex.list = ["/plex/b"];
    await follower.refresh();
    await catalog.scan();
    expect(catalog.roots).toEqual([resolve(b), resolve(extra)]);

    // Unreachable: the last list stands, and it says so once.
    plex.fail = true;
    await follower.refresh();
    await follower.refresh();
    await catalog.scan();
    expect(catalog.roots).toEqual([resolve(b), resolve(extra)]);
    expect(log.filter((l) => l.includes("couldn't read library folders")).length).toBe(1);
    expect(log.join("\n")).not.toContain("secret");

    plex.fail = false;
    await follower.refresh();
    expect(log.at(-1)).toContain("again");
  });

  it("the same folder twice (a repeat, or a Windows path in another case) counts once", async () => {
    const plex = source("Plex");
    plex.list = ["D:\\Movies", "d:/movies", "/data/tv", "/data/tv"];
    const follower = new LibraryFollower([plex], [], [], new Catalog([]), () => {});
    await follower.refresh();
    expect(follower.roots()).toEqual(["D:\\Movies", "/data/tv"]);
  });
});
