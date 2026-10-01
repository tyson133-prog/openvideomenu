/**
 * Runs every conformance case against the reference TypeScript implementation and the JSON Schema.
 * Other implementations (C#, native players) run the same case files with their own harness.
 *
 * Case file format (conformance/cases/*.json):
 *   description, level (0|1|2|3, the level the manifest uses), manifest,
 *   valid        — the reference validator accepts it
 *   schemaValid  — the JSON Schema accepts it (defaults to `valid`)
 *   errors[]     — issue paths the validator must report (subset match)
 *   warnings[]   — warning paths the validator must report (subset match)
 *   steps[]      — { event, expect: { mode?, menu?, focus?, intro?, buttonsReady?, overlay?, effects? } } run through the navigator;
 *                  `effects`, when given, must match exactly and in order.
 */
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";
import { describe, expect, it } from "vitest";
import { initialState, levelUsed, step, validateManifest, type Effect, type NavEvent, type NavState } from "../packages/core/src/index.js";

interface Case {
  description: string;
  level: number;
  valid: boolean;
  schemaValid?: boolean;
  errors?: string[];
  warnings?: string[];
  manifest: unknown;
  steps?: {
    event: NavEvent;
    /** overlay: null when none shows (Level 2). */
    expect?: Partial<Pick<NavState, "mode" | "menu" | "focus" | "intro" | "buttonsReady">> & { overlay?: NavState["overlay"] | null; effects?: Effect[] };
  }[];
}

const here = dirname(fileURLToPath(import.meta.url));
const casesDir = join(here, "cases");
const schema = JSON.parse(readFileSync(join(here, "../spec/schema/manifest.schema.json"), "utf8"));
const ajv = new Ajv2020.default({ allErrors: true, strict: false });
const schemaValidate = ajv.compile(schema);

const cases = readdirSync(casesDir)
  .filter((f) => f.endsWith(".json"))
  .sort()
  .map((file) => ({ file, c: JSON.parse(readFileSync(join(casesDir, file), "utf8")) as Case }));

describe.each(cases)("$file", ({ c }) => {
  it(`validator: ${c.valid ? "accepts" : "rejects"}`, () => {
    const result = validateManifest(c.manifest);
    const errorPaths = result.errors.map((e) => e.path);
    if (c.valid) expect(result.errors).toEqual([]);
    else expect(result.ok).toBe(false);
    for (const path of c.errors ?? []) expect(errorPaths, `missing error at ${path}`).toContain(path);
    const warningPaths = result.warnings.map((w) => w.path);
    for (const path of c.warnings ?? []) expect(warningPaths, `missing warning at ${path}`).toContain(path);
  });

  // Each case is for players at its level, so it must not use anything above it.
  it(`level: uses Level ${c.level}`, () => {
    expect(levelUsed(c.manifest)).toBe(c.level);
  });

  it(`schema: ${(c.schemaValid ?? c.valid) ? "accepts" : "rejects"}`, () => {
    const ok = schemaValidate(c.manifest);
    expect(ok, JSON.stringify(schemaValidate.errors, null, 2)).toBe(c.schemaValid ?? c.valid);
  });

  if (c.steps) {
    it("navigation steps", () => {
      const manifest = validateManifest(c.manifest).manifest!;
      let state = initialState();
      c.steps!.forEach(({ event, expect: want = {} }, i) => {
        const result = step(manifest, state, event);
        state = result.state;
        const label = `step ${i} (${JSON.stringify(event)})`;
        if (want.mode !== undefined) expect(state.mode, label).toBe(want.mode);
        if (want.menu !== undefined) expect(state.menu, label).toBe(want.menu);
        if (want.focus !== undefined) expect(state.focus, label).toBe(want.focus);
        if (want.intro !== undefined) expect(state.intro, label).toBe(want.intro);
        if (want.buttonsReady !== undefined) expect(state.buttonsReady, label).toBe(want.buttonsReady);
        if (want.overlay !== undefined) expect(state.overlay ?? null, label).toEqual(want.overlay);
        if (want.effects !== undefined) expect(result.effects, label).toEqual(want.effects);
      });
    });
  }
});
