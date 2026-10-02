/**
 * Conformance levels (spec §10): which level a manifest's menus use. Players support levels; a manifest
 * declares the level its menus need (`level`), and the validator checks that against what it uses.
 */

/** A level: 0 the fallback list, 1 menus, 2 enhanced (what a player shows and plays), 3 disc logic (program state). */
export type Level = 0 | 1 | 2 | 3;

/**
 * Level 2: fields that make a manifest need it wherever they appear (motion and music, timeouts, button
 * sets, transitions, angles, segments, track choices, cues, holds, keys, play-all order, and the
 * information-only prohibits and altRects).
 */
const LEVEL2_FIELDS = new Set([
  "video", "audio", "buttonsAt", "timeout", "buttonSets", "transition", "angles", "angleTracks", "segments",
  "subtitle", "angle", "end", "cues", "holds", "keys", "prohibits", "altRects", "order", "count", "cuePlayback",
]);

/** Level 2 actions. */
const LEVEL2_ACTIONS = new Set(["seek", "exit"]);

/** Level 3 (disc logic): actions that keep or change program state. */
const LEVEL3_ACTIONS = new Set(["set", "if", "do", "run", "saveResume", "clearResume", "timer", "cancelTimer", "requestParental"]);

/** Level 3: top-level fields of disc logic. */
const LEVEL3_TOP = ["vars", "actions", "resumePoint", "counters"];

/**
 * The level a manifest's menus use: 0 with only a fallback list, 1 with menus, 2 or 3 with those levels'
 * features. Any `{"var": name}` (a value read from a variable or player state) is Level 3. The fallback
 * list itself is Level 0 whatever its items carry.
 */
export function levelUsed(manifest: unknown): Level {
  if (!isObject(manifest)) return 0;
  let level: Level = manifest.menus !== undefined || manifest.firstPlay !== undefined ? 1 : 0;
  const raise = (to: Level) => {
    if (to > level) level = to;
  };
  if (LEVEL3_TOP.some((key) => manifest[key] !== undefined)) raise(3);
  const walk = (value: unknown) => {
    if (level === 3) return;
    if (Array.isArray(value)) {
      for (const v of value) walk(v);
      return;
    }
    if (!isObject(value)) return;
    const type = value.type;
    if (typeof type === "string") {
      if (LEVEL3_ACTIONS.has(type)) raise(3);
      else if (LEVEL2_ACTIONS.has(type)) raise(2);
    }
    if ("var" in value) raise(3);
    for (const [key, v] of Object.entries(value)) {
      if (key.startsWith("x-")) continue;
      if (LEVEL2_FIELDS.has(key)) raise(2);
      walk(v);
    }
  };
  for (const [key, v] of Object.entries(manifest)) {
    if (key === "fallback" || key === "title" || key === "source" || key.startsWith("x-")) continue;
    if (LEVEL2_FIELDS.has(key)) raise(2); // e.g. the manifest's keys, cuePlayback
    walk(v);
  }
  return level;
}

function isObject(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}
