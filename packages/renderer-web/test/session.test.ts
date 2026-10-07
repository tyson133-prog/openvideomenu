import { describe, expect, it } from "vitest";
import { manifestVersion } from "../src/load.js";

// Kept sessions are tied to a manifest version (spec §6.5.1): any change to the text gives another one.
describe("manifestVersion", () => {
  it("is the same for the same text", () => {
    expect(manifestVersion('{"format":"openvideomenu"}')).toBe(manifestVersion('{"format":"openvideomenu"}'));
  });

  it("changes when the text changes, even by one character", () => {
    const a = manifestVersion('{"vars":{"rabbit":0}}');
    const b = manifestVersion('{"vars":{"rabbit":1}}');
    expect(a).not.toBe(b);
  });

  it("is short and plain", () => {
    expect(manifestVersion("x".repeat(100_000))).toMatch(/^[0-9a-f]{8}-[0-9a-f]+$/);
  });
});
