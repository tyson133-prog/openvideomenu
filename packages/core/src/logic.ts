/**
 * The logic layer (Level 2): values, player-state variables, conditions and set operations.
 * Data only: every manifest stays JSON that any player can read; this is how the navigator evaluates it.
 */
import type { Scalar, SetOp, Test, Value } from "./types.js";

/**
 * Player-state variables: what any player knows about the viewer's setup and what's playing. Read with
 * {"var": "@name"}; the writable ones are changed with set (e.g. choosing a soundtrack).
 */
export const PLAYER_VARS: Record<string, { type: "number" | "string"; writable: boolean; default: Scalar }> = {
  "@audio": { type: "number", writable: true, default: 0 }, // current audio track, 1-based (0 = unknown)
  "@subtitle": { type: "number", writable: true, default: 0 }, // current subtitle track, 1-based; 0 = off
  "@angle": { type: "number", writable: true, default: 1 },
  "@focus": { type: "string", writable: true, default: "" }, // the highlighted button's id
  "@audioLanguage": { type: "string", writable: false, default: "" }, // preferred, ISO 639-1 ("fr")
  "@subtitleLanguage": { type: "string", writable: false, default: "" },
  "@menuLanguage": { type: "string", writable: false, default: "" },
  "@lastMedia": { type: "string", writable: false, default: "" }, // media id of the last playback
  "@lastChapter": { type: "number", writable: false, default: 0 }, // chapter it was in (0 = none)
  "@region": { type: "number", writable: false, default: 0 }, // 1-8; 0 = unknown
  "@parentalLevel": { type: "number", writable: false, default: 8 }, // 1-8; 8 = unrestricted
  "@country": { type: "string", writable: false, default: "" }, // ISO 3166-1 alpha-2
  "@aspect": { type: "string", writable: false, default: "16:9" }, // the TV's shape: "16:9" or "4:3"
};

export const TEST_OPS = ["eq", "ne", "lt", "le", "gt", "ge", "has"] as const;
export const SET_OPS: readonly SetOp[] = ["set", "add", "sub", "mul", "div", "mod", "and", "or", "xor", "random"];

/** What the logic reads and writes: the manifest's own variables, player state, and the focused button. */
export interface LogicState {
  vars: Record<string, Scalar>;
  player: Record<string, Scalar>;
  focus?: string;
  seed: number;
}

export function readVar(s: LogicState, name: string): Scalar {
  if (name === "@focus") return s.focus ?? "";
  if (name.startsWith("@")) return s.player[name] ?? PLAYER_VARS[name]?.default ?? 0;
  return s.vars[name] ?? 0;
}

export function valueOf(s: LogicState, v: Value): Scalar {
  return typeof v === "object" ? readVar(s, v.var) : v;
}

export function evalTest(s: LogicState, t: Test): boolean {
  if ("all" in t) return t.all.every((x) => evalTest(s, x));
  if ("any" in t) return t.any.some((x) => evalTest(s, x));
  if ("not" in t) return !evalTest(s, t.not);
  const left = readVar(s, t.var);
  for (const op of TEST_OPS) {
    if (!(op in t)) continue;
    const right = valueOf(s, t[op] as Value);
    switch (op) {
      case "eq":
        return left === right;
      case "ne":
        return left !== right;
      case "has":
        return typeof left === "number" && typeof right === "number" && (left & right) !== 0;
      default: {
        if (typeof left !== "number" || typeof right !== "number") return false;
        return op === "lt" ? left < right : op === "le" ? left <= right : op === "gt" ? left > right : left >= right;
      }
    }
  }
  return false;
}

/**
 * The next random number, 1..max, from the state's seed (a 31-bit LCG: seed = (seed * 1103515245 + 12345)
 * mod 2^31). Specified exactly so every implementation of the navigator agrees; players seed it randomly.
 */
export function nextRandom(s: LogicState, max: number): number {
  s.seed = Number((BigInt(s.seed) * 1103515245n + 12345n) % 2147483648n);
  return 1 + (s.seed % max);
}

/** The new value of a variable after set, or undefined when the operation leaves it unchanged. */
export function applyOp(s: LogicState, current: Scalar, op: SetOp, operand: Scalar): Scalar | undefined {
  if (op === "set") return operand;
  if (typeof current !== "number" || typeof operand !== "number") return undefined;
  switch (op) {
    case "add":
      return current + operand;
    case "sub":
      return current - operand;
    case "mul":
      return current * operand;
    case "div":
      return operand === 0 ? undefined : Math.trunc(current / operand);
    case "mod":
      return operand === 0 ? undefined : current % operand;
    case "and":
      return current & operand;
    case "or":
      return current | operand;
    case "xor":
      return current ^ operand;
    case "random":
      return operand < 1 ? undefined : nextRandom(s, operand);
  }
  return undefined;
}
