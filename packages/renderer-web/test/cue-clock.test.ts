import { describe, expect, it } from "vitest";
import { CueClock } from "../src/adapters.js";

/** A clock that writes what it reports into a log. */
function clock(holds: { at: number; for: number | "input" }[], end?: number) {
  const log: string[] = [];
  const c = new CueClock(
    [
      { id: "before", at: 9 },
      { id: "on", at: 10 },
      { id: "over", from: 8, to: 10 },
    ],
    end,
    { cue: (id) => log.push(`cue ${id}`), cueEnd: (id) => log.push(`end ${id}`), ended: () => log.push("ended"), hold: (h) => log.push(`hold ${h.at}`) },
    holds,
  );
  return { c, log };
}

describe("CueClock holds (spec 6.3)", () => {
  it("holds before the cues at its time, with an overlay ending there still up", () => {
    const { c, log } = clock([{ at: 10, for: "input" }]);
    c.start(0);
    c.tick(8.5);
    c.tick(10.2); // overshoots the still frame
    expect(log).toEqual(["cue over", "cue before", "hold 10"]);
    c.tick(10.5); // ignored while holding
    expect(c.held).toBeDefined();
    // Going on: the cue at the hold's time runs, and the overlay that ended there goes.
    c.release();
    expect(log.slice(3)).toEqual(["cue on", "end over"]);
    c.tick(10.3);
    expect(log).toHaveLength(5);
  });

  it("doesn't hold again on the same pass, but does when playback comes round again", () => {
    const { c, log } = clock([{ at: 10, for: 2 }]);
    c.start(9.5);
    c.tick(10.1);
    c.release();
    c.tick(10.3);
    c.tick(5); // a seek back (a loop)
    c.tick(9.9);
    c.tick(10.1);
    expect(log.filter((l) => l.startsWith("hold"))).toEqual(["hold 10", "hold 10"]);
  });

  it("skips a hold when seeking past it", () => {
    const { c, log } = clock([{ at: 10, for: "input" }]);
    c.start(0);
    c.tick(1);
    c.tick(30);
    expect(log.some((l) => l.startsWith("hold"))).toBe(false);
  });

  it("holds a still at the very end before the title ends", () => {
    const { c, log } = clock([{ at: 20, for: "input" }]);
    c.start(15);
    c.tick(19.9);
    expect(c.finish()).toBe(false);
    expect(log).toEqual(["hold 20"]);
    expect(c.release()).toBe(true);
    expect(log).toEqual(["hold 20", "ended"]);
  });
});
