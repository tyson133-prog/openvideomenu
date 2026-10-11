import { describe, expect, it } from "vitest";
import { placeFromPath, titleWithPlace } from "../src/place.js";

describe("season and volume from a menu's path", () => {
  it.each([
    ["TV/All in the Family/Season 05/All in the Family (1971) - Season 05 - Disc 2.ovmz", { season: 5, volume: 2 }],
    ["Show/Season 05/Disc 2/Disc 2.ovmz", { season: 5, volume: 2 }],
    ["Show/Season 1/Show.openvideomenu.json", { season: 1 }],
    ["Show/Specials/Show - Disk 1.ovmz", { season: 0, volume: 1 }],
    ["Show/Show S03D4.ovmz", { season: 3, volume: 4 }],
    ["Show/Season_02/show.s02.d1.ovmz", { season: 2, volume: 1 }],
    ["Movies/Heat (1995)/Disc 2/Heat (1995).ovmz", { volume: 2 }],
    ["Movies/Season of the Witch (2011)/Season of the Witch (2011).ovmz", {}],
    ["Movies/Disclosure (1994)/Disclosure (1994).ovmz", {}],
    ["Movies/Heat (1995)/Heat (1995).ovmz", {}],
  ])("%s", (path, want) => {
    expect(placeFromPath(path)).toEqual(want);
  });

  it("the nearest name wins", () => {
    expect(placeFromPath("Show/Season 01/Season 02 extras/Disc 3/menu Disc 4.ovmz")).toEqual({ season: 2, volume: 4 });
  });

  it("keeps what the manifest says and fills only the rest", () => {
    const path = "Show/Season 05/Show - Disc 2.ovmz";
    expect(titleWithPlace({ name: "Show", season: 4 }, path)).toEqual({ name: "Show", season: 4, volume: 2 });
    expect(titleWithPlace({ name: "Show", season: 4, volume: 1 }, path)).toEqual({ name: "Show", season: 4, volume: 1 });
    expect(titleWithPlace({ name: "Film", year: 1995 }, "Film (1995)/Film (1995).ovmz")).toEqual({ name: "Film", year: 1995 });
  });
});
