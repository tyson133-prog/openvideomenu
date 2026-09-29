import { describe, expect, it } from "vitest";
import { canvasAspect, parseAspect, parseManifest, resolveMedia, resolvePath } from "../src/index.js";

describe("parseAspect", () => {
  it.each([
    ["16:9", 16 / 9],
    ["4:3", 4 / 3],
    ["1.85", 1.85],
    [" 2.39 : 1 ", 2.39],
    [1.5, 1.5],
  ])("%s", (input, expected) => expect(parseAspect(input)).toBeCloseTo(expected));

  it.each(["", "wide", "0:9", -1, "16:0"])("rejects %s", (input) => expect(parseAspect(input)).toBeUndefined());

  it("defaults to square pixels", () => {
    expect(canvasAspect({ canvas: { width: 1280, height: 720 } })).toBeCloseTo(16 / 9);
    expect(canvasAspect({ canvas: { width: 720, height: 480, aspect: "4:3" } })).toBeCloseTo(4 / 3);
  });
});

describe("resolve", () => {
  const manifestUrl = "http://localhost:8080/library/Sintel%20(2010)/Sintel%20(2010).openvideomenu.json";

  it("resolves relative paths with spaces against the manifest", () => {
    expect(resolvePath("Trailers/Sintel (2010)-trailer.mp4", manifestUrl)).toBe(
      "http://localhost:8080/library/Sintel%20(2010)/Trailers/Sintel%20(2010)-trailer.mp4",
    );
  });

  it("keeps absolute URLs", () => {
    expect(resolvePath("https://cdn.example/a.m3u8", manifestUrl)).toBe("https://cdn.example/a.m3u8");
  });

  it("hands scheme refs to the host resolver", () => {
    const resolvers = { jellyfin: (r: { id: string }) => `/Videos/${r.id}/stream` };
    expect(resolveMedia({ scheme: "jellyfin", id: "abc" }, manifestUrl, resolvers)).toBe("/Videos/abc/stream");
    expect(resolveMedia({ scheme: "unknown", id: "abc" }, manifestUrl, resolvers)).toBeUndefined();
  });
});

describe("parseManifest", () => {
  it("reports bad JSON instead of throwing", () => {
    const result = parseManifest("{ not json");
    expect(result.ok).toBe(false);
    expect(result.errors[0]!.message).toMatch(/Not valid JSON/);
  });
});

describe("menuAssets", () => {
  it("lists menu art but never media refs or absolute URLs", async () => {
    const { readFileSync } = await import("node:fs");
    const { menuAssets } = await import("../src/index.js");
    const c = JSON.parse(readFileSync(new URL("../../../conformance/cases/08-motion-buttonsat.json", import.meta.url), "utf8"));
    expect(menuAssets(c.manifest)).toEqual(["menus/main.jpg", "menus/main.m4a", "menus/main.mp4"]);
    const t = JSON.parse(readFileSync(new URL("../../../conformance/cases/09-transition-skip.json", import.meta.url), "utf8"));
    expect(menuAssets(t.manifest)).toEqual(["menus/extras.mp4", "menus/main.mp4", "menus/to-extras.mp4"]);
    const d = JSON.parse(readFileSync(new URL("../../../conformance/cases/07-dvd-shaped.json", import.meta.url), "utf8"));
    const assets = menuAssets(d.manifest);
    expect(assets).toContain("menus/vts1-pgc2-b1-selected.png");
    expect(assets.some((a: string) => a.endsWith(".mkv"))).toBe(false);
  });

  it("follows a resume's otherwise to the transition it plays", async () => {
    const { menuAssets } = await import("../src/index.js");
    const manifest = {
      format: "openvideomenu", version: "0.1", title: { name: "T" },
      media: { feature: { ref: "f.mp4" } },
      menus: {
        main: { canvas: { width: 720, height: 480 }, buttons: [{ id: "b", rect: [0, 0, 1, 1], action: { type: "resume", otherwise: { type: "goto", menu: "pick", transition: "menus/to-pick.mp4" } } }] },
        pick: { canvas: { width: 720, height: 480 }, buttons: [{ id: "p", rect: [0, 0, 1, 1], action: { type: "play", media: "feature" } }] },
      },
      rootMenu: "main",
      fallback: [{ label: "Movie", items: [{ label: "Play", action: { type: "play", media: "feature" } }] }],
    };
    expect(menuAssets(manifest as never)).toEqual(["menus/to-pick.mp4"]);
  });

  it("follows set actions to the transition their then-action plays", async () => {
    const { menuAssets } = await import("../src/index.js");
    const manifest = {
      format: "openvideomenu", version: "0.1", title: { name: "T" }, vars: { audio: 1 },
      media: { feature: { ref: "f.mp4" } },
      menus: {
        main: { canvas: { width: 720, height: 480 }, buttons: [{ id: "b", rect: [0, 0, 1, 1], action: { type: "goto", menu: "lang" } }] },
        lang: {
          canvas: { width: 720, height: 480 },
          buttons: [{ id: "c", rect: [0, 0, 1, 1], action: { type: "set", var: "audio", value: 2, then: { type: "goto", menu: "main", transition: "menus/back.mp4" } } }],
        },
      },
      rootMenu: "main",
      fallback: [{ label: "Movie", items: [{ label: "Play", action: { type: "play", media: "feature" } }] }],
    };
    expect(menuAssets(manifest as never)).toEqual(["menus/back.mp4"]);
  });
});
