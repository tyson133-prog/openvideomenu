import { FORMAT_ID, FORMAT_VERSION, isFormatId } from "./constants.js";
import { levelUsed } from "./levels.js";
import { MENU_KEYS, PLAYER_VARS, PROHIBITIONS, SET_OPS, TEST_OPS } from "./logic.js";
import type { Action, Manifest, Menu, PlayItem } from "./types.js";

export interface Issue {
  /** JSON-pointer-like location, e.g. "/menus/main/buttons/2/nav/up". */
  path: string;
  message: string;
}

export interface ValidationResult {
  ok: boolean;
  errors: Issue[];
  /** Problems that don't stop a player (unknown fields, newer minor version, off-canvas rects...). */
  warnings: Issue[];
  /** Present when ok is true. */
  manifest?: Manifest;
}

const KNOWN_KEYS: Record<string, readonly string[]> = {
  manifest: ["format", "version", "title", "source", "media", "menus", "rootMenu", "firstPlay", "fallback", "vars", "actions", "resumePoint", "keys", "counters", "level", "cuePlayback", "$schema"],
  title: ["name", "year", "ids"],
  source: ["kind", "generator"],
  media: ["ref", "segments", "label", "kind", "duration", "chapters", "angles", "angleTracks", "cues", "keys", "prohibits", "prohibitsAt", "holds", "number"],
  prohibitWindow: ["from", "to", "ops"],
  hold: ["at", "for"],
  segment: ["media", "start", "end"],
  chapter: ["time", "label", "thumbnail"],
  menu: ["canvas", "background", "label", "defaultButton", "autoNav", "buttons", "buttonsAt", "back", "timeout", "keys", "prohibits", "buttonSets"],
  buttonSet: ["from", "to", "buttons", "defaultButton", "autoNav"],
  timeout: ["after", "action", "restartOnInput"],
  cue: ["at", "action"],
  overlay: ["from", "to", "canvas", "buttons", "defaultButton", "autoNav"],
  canvas: ["width", "height", "aspect"],
  background: ["image", "color", "video", "audio"],
  loopingMedia: ["src", "loopStart"],
  button: ["id", "rect", "label", "a11yLabel", "showLabel", "states", "nav", "autoAction", "action", "altRects"],
  state: ["image", "rect", "textColor", "fill"],
  fallbackGroup: ["label", "hidden", "items"],
  fallbackItem: ["label", "action"],
};

const ACTION_KEYS: Record<string, readonly string[]> = {
  play: ["type", "media", "chapter", "start", "end", "after", "audio", "subtitle", "angle", "cues", "keys", "prohibits", "prohibitsAt", "holds"],
  playAll: ["type", "items", "after", "order", "count"],
  set: ["type", "var", "value", "op", "then"],
  if: ["type", "test", "then", "else"],
  do: ["type", "steps"],
  goto: ["type", "menu", "button", "transition", "history"],
  back: ["type"],
  resume: ["type", "otherwise"],
  activate: ["type", "button"],
  run: ["type", "action"],
  saveResume: ["type", "media", "item", "at", "chapter", "queue", "after"],
  seek: ["type", "at", "chapter"],
  timer: ["type", "after", "action"],
  cancelTimer: ["type"],
  requestParental: ["type", "level", "then", "else"],
  exit: ["type"],
  clearResume: ["type"],
};

const isObject = (v: unknown): v is Record<string, unknown> =>
  typeof v === "object" && v !== null && !Array.isArray(v);
const isNonEmptyString = (v: unknown): v is string => typeof v === "string" && v.length > 0;
const isFiniteNumber = (v: unknown): v is number => typeof v === "number" && Number.isFinite(v);

/** Parse JSON text and validate it. */
export function parseManifest(text: string): ValidationResult {
  let data: unknown;
  try {
    data = JSON.parse(text);
  } catch (e) {
    return { ok: false, errors: [{ path: "", message: `Not valid JSON: ${(e as Error).message}` }], warnings: [] };
  }
  return validateManifest(data);
}

/** Validate an already-parsed manifest: structure first, then cross-references. */
export function validateManifest(data: unknown): ValidationResult {
  const v = new Validator();
  v.manifest(data);
  const ok = v.errors.length === 0;
  return { ok, errors: v.errors, warnings: v.warnings, manifest: ok ? (data as Manifest) : undefined };
}

class Validator {
  errors: Issue[] = [];
  warnings: Issue[] = [];

  private error(path: string, message: string) {
    this.errors.push({ path, message });
  }
  private warn(path: string, message: string) {
    this.warnings.push({ path, message });
  }

  /** Unknown non-extension keys are allowed (charter rule 3) but usually a typo, so warn. */
  private unknownKeys(obj: Record<string, unknown>, known: readonly string[], path: string) {
    for (const key of Object.keys(obj)) {
      if (!known.includes(key) && !key.startsWith("x-")) {
        this.warn(`${path}/${key}`, `Unknown property "${key}" (ignored). Vendor extensions must start with "x-".`);
      }
    }
  }

  manifest(data: unknown) {
    if (!isObject(data)) {
      this.error("", "Manifest must be a JSON object.");
      return;
    }
    this.unknownKeys(data, KNOWN_KEYS.manifest!, "");

    if (!isFormatId(data.format)) {
      this.error("/format", `Must be "${FORMAT_ID}".`);
    }
    this.version(data.version);
    this.title(data.title);
    if (data.source !== undefined) this.source(data.source);

    this.vars(data.vars);
    if (data.cuePlayback !== undefined && data.cuePlayback !== "interrupt" && data.cuePlayback !== "replace")
      this.error("/cuePlayback", 'Must be "interrupt" or "replace".');
    this.replacing = data.cuePlayback === "replace";
    if (data.counters !== undefined) {
      if (!Array.isArray(data.counters)) this.error("/counters", "Must be a list of variable names.");
      else
        data.counters.forEach((name, i) => {
          if (this.varTypes.get(name as string) !== "number") this.error(`/counters/${i}`, `No integer variable "${String(name)}" in /vars.`);
        });
    }
    if (data.resumePoint !== undefined && data.resumePoint !== "untilEnd" && data.resumePoint !== "untilUsed")
      this.error("/resumePoint", 'Must be "untilEnd" or "untilUsed".');
    this.collectActions(data.actions);
    const mediaIds = this.media(data.media);
    const menus = this.menus(data.menus, mediaIds);
    // Media cues and keys run actions, so they're checked once every menu is known.
    if (isObject(data.media))
      for (const [id, m] of Object.entries(data.media))
        if (isObject(m)) {
          if (m.cues !== undefined) this.cues(m.cues, `/media/${id}/cues`, mediaIds, menus);
          if (m.keys !== undefined) this.keys(m.keys, `/media/${id}/keys`, mediaIds, menus, true);
          if (m.prohibits !== undefined) this.prohibits(m.prohibits, `/media/${id}/prohibits`);
          if (m.prohibitsAt !== undefined) this.prohibitsAt(m.prohibitsAt, `/media/${id}/prohibitsAt`);
          if (m.holds !== undefined) this.holds(m.holds, `/media/${id}/holds`);
        }
    if (data.keys !== undefined) this.keys(data.keys, "/keys", mediaIds, menus, true);

    if (data.rootMenu !== undefined) {
      if (!isNonEmptyString(data.rootMenu)) this.error("/rootMenu", "Must be a menu id.");
      else if (!menus.has(data.rootMenu)) this.error("/rootMenu", `No menu "${data.rootMenu}".`);
    } else if (menus.size > 0) {
      this.error("/rootMenu", "Required when the manifest has menus.");
    }

    if (data.firstPlay !== undefined) this.action(data.firstPlay, "/firstPlay", mediaIds, menus);
    for (const [name, body] of this.named) this.namedAction(name, body, mediaIds, menus);

    this.fallback(data.fallback, mediaIds, menus);
    this.level(data);
  }

  /** The level the menus need (spec §10), against the level they use. */
  private level(data: Record<string, unknown>) {
    const used = levelUsed(data);
    if (data.level !== undefined) {
      if (!(Number.isInteger(data.level) && (data.level as number) >= 0 && (data.level as number) <= 3)) this.error("/level", "Must be a level from 0 to 3.");
      else if ((data.level as number) < used)
        this.warn("/level", `The menus use Level ${used}; a player at Level ${String(data.level)} would show them without what they need. Declare "level": ${used}.`);
    } else if (used === 3) {
      this.warn("/level", 'The menus use Level 3 (disc logic): declare "level": 3, so players below it show the fallback list.');
    }
  }

  /** Declared variables and their types (a variable keeps the type of its starting value). */
  private varTypes = new Map<string, "number" | "string">();

  /** Above 0 while checking cues: playback started there returns to the interrupted one. */
  private cueDepth = 0;

  /** Media id → how many angles it has (1 without angles). */
  private angleCounts = new Map<string, number>();

  /** Level 2: playback started during playback replaces the title playing (cuePlayback "replace"), so it may have an after. */
  private replacing = false;

  /** Above 0 while checking named actions, which may run during playback wherever they're run from. */
  private namedDepth = 0;

  /** Level 3: the manifest's named actions, by name. */
  private named = new Map<string, unknown>();

  /** Named actions that start playback with an `after` (also through other named actions): not for cues. */
  private playsWithAfter = new Set<string>();

  /**
   * Level 3: note every named action, and which of them start playback with an `after` when run
   * (directly or through the named actions they run), before any action that may run them is checked.
   */
  private collectActions(value: unknown) {
    if (value === undefined) return;
    if (!isObject(value)) {
      this.error("/actions", "Must be an object of name → action.");
      return;
    }
    const runs = new Map<string, Set<string>>();
    for (const [name, body] of Object.entries(value)) {
      this.named.set(name, body);
      const targets = new Set<string>();
      runs.set(name, targets);
      sameContext(body, (a) => {
        if ((a.type === "play" || a.type === "playAll") && a.after !== undefined) this.playsWithAfter.add(name);
        if (a.type === "run" && typeof a.action === "string") targets.add(a.action);
      });
    }
    for (let changed = true; changed; ) {
      changed = false;
      for (const [name, targets] of runs)
        if (!this.playsWithAfter.has(name) && [...targets].some((t) => this.playsWithAfter.has(t))) {
          this.playsWithAfter.add(name);
          changed = true;
        }
    }
  }

  /** Check a named action's body. One that only runs other named actions in a circle never does anything. */
  private namedAction(name: string, body: unknown, mediaIds: Map<string, number>, menus: Map<string, Set<string>>) {
    const path = `/actions/${name}`;
    const seen = new Set<string>([name]);
    for (let a = body; isObject(a) && a.type === "run" && typeof a.action === "string"; a = this.named.get(a.action)) {
      if (seen.has(a.action)) {
        this.error(path, `Only runs named actions in a circle (back to "${a.action}"); it would never do anything.`);
        return;
      }
      seen.add(a.action);
    }
    this.namedDepth++;
    this.action(body, path, mediaIds, menus);
    this.namedDepth--;
  }

  /** The action a run (or a chain of them) comes to, or the action itself. */
  private resolveRun(value: unknown): unknown {
    const seen = new Set<string>();
    while (isObject(value) && value.type === "run" && typeof value.action === "string" && !seen.has(value.action)) {
      seen.add(value.action);
      value = this.named.get(value.action);
    }
    return value;
  }

  private vars(value: unknown) {
    if (value === undefined) return;
    if (!isObject(value)) {
      this.error("/vars", "Must be an object of name → integer or text.");
      return;
    }
    for (const [name, v] of Object.entries(value)) {
      if (name.startsWith("@")) this.error(`/vars/${name}`, 'Names starting with "@" are player state, not the manifest\'s own variables.');
      else if (Number.isInteger(v)) this.varTypes.set(name, "number");
      else if (typeof v === "string") this.varTypes.set(name, "string");
      else this.error(`/vars/${name}`, "Must be an integer or text.");
    }
  }

  /** The type of a variable the manifest may read (its own, or "@" player state), or undefined if there's none. */
  private typeOfVar(name: unknown): "number" | "string" | undefined {
    if (typeof name !== "string") return undefined;
    return name.startsWith("@") ? PLAYER_VARS[name]?.type : this.varTypes.get(name);
  }

  /** Check a {var} reference; returns its type. */
  private varRef(value: Record<string, unknown>, path: string): "number" | "string" | undefined {
    const t = this.typeOfVar(value.var);
    if (!t) this.error(`${path}/var`, `No variable "${String(value.var)}" in /vars or player state.`);
    return t;
  }

  /** A literal value (integer or text) or {var}; returns its type when known. */
  private value(value: unknown, path: string): "number" | "string" | undefined {
    if (isObject(value)) return this.varRef(value, path);
    if (Number.isInteger(value)) return "number";
    if (typeof value === "string") return "string";
    this.error(path, 'Must be an integer, text, or {"var": name}.');
    return undefined;
  }

  private varOrNumber(value: unknown, path: string, min: number) {
    if (isObject(value)) {
      const t = this.varRef(value, path);
      if (t === "string") this.error(`${path}/var`, `Variable "${String(value.var)}" holds text; a track number is needed.`);
    } else if (!Number.isInteger(value) || (value as number) < min) {
      this.error(path, `Must be an integer ≥ ${min}, or {"var": name}.`);
    }
  }

  /** A condition: one comparison of a variable, or all/any/not of other conditions. */
  private test(value: unknown, path: string) {
    if (!isObject(value)) {
      this.error(path, 'Must be a condition: {"var", "eq"|"ne"|"lt"|"le"|"gt"|"ge"|"has": value}, or all/any/not.');
      return;
    }
    for (const key of ["all", "any"] as const) {
      if (key in value) {
        const list = value[key];
        if (!Array.isArray(list) || list.length === 0) this.error(`${path}/${key}`, "Must be a list of at least one condition.");
        else list.forEach((t, i) => this.test(t, `${path}/${key}/${i}`));
        return;
      }
    }
    if ("not" in value) {
      this.test(value.not, `${path}/not`);
      return;
    }
    const ops = TEST_OPS.filter((op) => op in value);
    this.unknownKeys(value, ["var", ...TEST_OPS], path);
    const left = this.varRef(value, path);
    if (ops.length !== 1) {
      this.error(path, "Needs exactly one comparison: eq, ne, lt, le, gt, ge or has.");
      return;
    }
    const op = ops[0]!;
    const right = this.value(value[op], `${path}/${op}`);
    if (op !== "eq" && op !== "ne" && (left === "string" || right === "string"))
      this.error(`${path}/${op}`, `"${op}" compares integers; text can only be compared with eq or ne.`);
    else if (left && right && left !== right) this.error(`${path}/${op}`, `Compares ${left === "number" ? "an integer" : "text"} with ${right === "number" ? "an integer" : "text"}.`);
  }

  /** A button reference: an id of the menu, or a variable holding one. */
  private buttonRef(value: unknown, path: string, buttons: Set<string>, where: string) {
    if (isObject(value)) {
      if (this.varRef(value, path) === "number") this.error(`${path}/var`, `Variable "${String(value.var)}" holds an integer; a button id is text.`);
    } else if (!(isNonEmptyString(value) && buttons.has(value))) {
      this.error(path, `No button "${String(value)}" in ${where}.`);
    }
  }

  private version(value: unknown) {
    if (!isNonEmptyString(value) || !/^\d+\.\d+$/.test(value)) {
      this.error("/version", 'Must be "MAJOR.MINOR", e.g. "0.1".');
      return;
    }
    const [major, minor] = value.split(".").map(Number) as [number, number];
    const [ourMajor, ourMinor] = FORMAT_VERSION.split(".").map(Number) as [number, number];
    if (major !== ourMajor) {
      this.error("/version", `Major version ${major} is not supported by this reader (supports ${ourMajor}.x).`);
    } else if (minor > ourMinor) {
      this.warn("/version", `Written for ${value}; this reader knows ${FORMAT_VERSION}. Newer fields will be ignored.`);
    }
  }

  private title(value: unknown) {
    if (!isObject(value)) {
      this.error("/title", "Required object with at least a name.");
      return;
    }
    this.unknownKeys(value, KNOWN_KEYS.title!, "/title");
    if (!isNonEmptyString(value.name)) this.error("/title/name", "Required.");
    if (value.year !== undefined && !Number.isInteger(value.year)) this.error("/title/year", "Must be an integer.");
    if (value.ids !== undefined) {
      if (!isObject(value.ids)) this.error("/title/ids", "Must be an object of strings.");
      else
        for (const [k, id] of Object.entries(value.ids))
          if (typeof id !== "string") this.error(`/title/ids/${k}`, "Must be a string.");
    }
  }

  private source(value: unknown) {
    if (!isObject(value)) {
      this.error("/source", "Must be an object.");
      return;
    }
    this.unknownKeys(value, KNOWN_KEYS.source!, "/source");
    if (!isNonEmptyString(value.kind)) this.error("/source/kind", "Required.");
    if (value.generator !== undefined && typeof value.generator !== "string")
      this.error("/source/generator", "Must be a string.");
  }

  /** Returns media id → chapter count. */
  private media(value: unknown): Map<string, number> {
    const ids = new Map<string, number>();
    if (!isObject(value) || Object.keys(value).length === 0) {
      this.error("/media", "Required object with at least one media entry.");
      return ids;
    }
    for (const [id, m] of Object.entries(value)) {
      const path = `/media/${id}`;
      if (!isObject(m)) {
        this.error(path, "Must be an object.");
        continue;
      }
      this.unknownKeys(m, KNOWN_KEYS.media!, path);
      // Level 2: made of segments of other media (checked below, once every id is known) or a ref.
      if (m.segments === undefined) this.mediaRef(m.ref, `${path}/ref`);
      else if (m.ref !== undefined) this.error(`${path}/segments`, "Give a ref or segments, not both.");
      else if (m.angles !== undefined || m.angleTracks !== undefined)
        this.error(`${path}/${m.angles !== undefined ? "angles" : "angleTracks"}`, "A media made of segments has no angles of its own; its segments' media do.");
      let angles = 1;
      if (m.angles !== undefined) {
        if (!Array.isArray(m.angles) || m.angles.length === 0) this.error(`${path}/angles`, "Must be a list of at least one media ref.");
        else {
          m.angles.forEach((a, i) => this.mediaRef(a, `${path}/angles/${i}`));
          angles = 1 + m.angles.length;
        }
      }
      if (m.angleTracks !== undefined) {
        if (m.angles !== undefined) this.error(`${path}/angleTracks`, "Give angles as files (angles) or as video tracks (angleTracks), not both.");
        else if (!Number.isInteger(m.angleTracks) || (m.angleTracks as number) < 2) this.error(`${path}/angleTracks`, "Must be an integer ≥ 2.");
        else angles = m.angleTracks as number;
      }
      this.angleCounts.set(id, angles);
      if (m.label !== undefined && typeof m.label !== "string") this.error(`${path}/label`, "Must be a string.");
      if (m.kind !== undefined && typeof m.kind !== "string") this.error(`${path}/kind`, "Must be a string.");
      if (m.number !== undefined && !Number.isInteger(m.number)) this.error(`${path}/number`, "Must be an integer.");
      if (m.duration !== undefined && !(isFiniteNumber(m.duration) && m.duration >= 0))
        this.error(`${path}/duration`, "Must be a non-negative number of seconds.");

      let chapterCount = 0;
      if (m.chapters !== undefined) {
        if (!Array.isArray(m.chapters)) this.error(`${path}/chapters`, "Must be an array.");
        else {
          chapterCount = m.chapters.length;
          let previous = -1;
          m.chapters.forEach((c, i) => {
            const cp = `${path}/chapters/${i}`;
            if (!isObject(c)) {
              this.error(cp, "Must be an object.");
              return;
            }
            this.unknownKeys(c, KNOWN_KEYS.chapter!, cp);
            if (!isFiniteNumber(c.time) || c.time < 0) this.error(`${cp}/time`, "Must be a non-negative number.");
            else {
              if (c.time <= previous) this.error(`${cp}/time`, "Chapter times must increase.");
              previous = c.time;
            }
            if (c.label !== undefined && typeof c.label !== "string") this.error(`${cp}/label`, "Must be a string.");
            if (c.thumbnail !== undefined && !isNonEmptyString(c.thumbnail))
              this.error(`${cp}/thumbnail`, "Must be a path or URL.");
          });
        }
      }
      ids.set(id, chapterCount);
    }
    for (const [id, m] of Object.entries(value)) if (isObject(m) && m.segments !== undefined) this.segments(m.segments, id, value);
    return ids;
  }

  /** Level 2: a media made of segments of other media (spec 4.2). */
  private segments(value: unknown, id: string, media: Record<string, unknown>) {
    const path = `/media/${id}/segments`;
    if (!Array.isArray(value) || value.length === 0) {
      this.error(path, "Must be a list of at least one segment {media, start?, end?}.");
      return;
    }
    let angles = 1;
    value.forEach((seg, i) => {
      const sp = `${path}/${i}`;
      if (!isObject(seg)) {
        this.error(sp, "Must be a segment {media, start?, end?}.");
        return;
      }
      this.unknownKeys(seg, KNOWN_KEYS.segment!, sp);
      const source = isNonEmptyString(seg.media) ? media[seg.media] : undefined;
      if (!isObject(source)) this.error(`${sp}/media`, `No media "${String(seg.media)}".`);
      else if (source.segments !== undefined) this.error(`${sp}/media`, `Media "${String(seg.media)}" is made of segments; segments don't nest.`);
      else angles = Math.max(angles, this.angleCounts.get(seg.media as string) ?? 1);
      const okStart = seg.start === undefined || (isFiniteNumber(seg.start) && seg.start >= 0);
      if (!okStart) this.error(`${sp}/start`, "Must be a non-negative number of seconds.");
      if (seg.end === undefined) {
        if (i < value.length - 1) this.error(`${sp}/end`, "Required on every segment but the last.");
      } else if (!(isFiniteNumber(seg.end) && seg.end > (okStart && isFiniteNumber(seg.start) ? seg.start : 0)))
        this.error(`${sp}/end`, "Must be a number of seconds after start.");
    });
    // A play's angle applies to the segments whose media have angles.
    this.angleCounts.set(id, angles);
  }

  private mediaRef(ref: unknown, path: string) {
    if (isObject(ref)) {
      if (!isNonEmptyString(ref.scheme) || !isNonEmptyString(ref.id)) this.error(path, "Object refs need non-empty scheme and id.");
    } else if (!isNonEmptyString(ref)) {
      this.error(path, "Required: a relative path, URL, or {scheme, id}.");
    }
  }

  /** Returns menu id → set of its button ids. */
  private menus(value: unknown, mediaIds: Map<string, number>): Map<string, Set<string>> {
    const menus = new Map<string, Set<string>>();
    if (value === undefined) return menus;
    if (!isObject(value)) {
      this.error("/menus", "Must be an object of menus.");
      return menus;
    }
    // First pass: collect button ids (the menu's own and its button sets') so goto targets can be
    // checked in any order.
    for (const [id, m] of Object.entries(value)) {
      const buttons = new Set<string>();
      const lists = isObject(m) ? [m.buttons, ...(Array.isArray(m.buttonSets) ? m.buttonSets.map((set) => (isObject(set) ? set.buttons : undefined)) : [])] : [];
      for (const list of lists) if (Array.isArray(list)) for (const b of list) if (isObject(b) && isNonEmptyString(b.id)) buttons.add(b.id);
      menus.set(id, buttons);
    }
    for (const [id, m] of Object.entries(value)) this.menu(m, `/menus/${id}`, mediaIds, menus);
    return menus;
  }

  private menu(value: unknown, path: string, mediaIds: Map<string, number>, menus: Map<string, Set<string>>) {
    if (!isObject(value)) {
      this.error(path, "Must be an object.");
      return;
    }
    this.unknownKeys(value, KNOWN_KEYS.menu!, path);
    const canvas = this.canvas(value.canvas, `${path}/canvas`);

    if (value.background !== undefined) {
      const bg = value.background;
      if (!isObject(bg)) this.error(`${path}/background`, "Must be an object.");
      else {
        this.unknownKeys(bg, KNOWN_KEYS.background!, `${path}/background`);
        if (bg.image !== undefined && !isNonEmptyString(bg.image))
          this.error(`${path}/background/image`, "Must be a path or URL.");
        if (bg.color !== undefined && typeof bg.color !== "string")
          this.error(`${path}/background/color`, "Must be a colour string.");
        for (const key of ["video", "audio"] as const)
          if (bg[key] !== undefined) this.loopingMedia(bg[key], `${path}/background/${key}`);
      }
    }
    if (value.autoNav !== undefined && typeof value.autoNav !== "boolean")
      this.error(`${path}/autoNav`, "Must be true or false.");
    if (value.buttonsAt !== undefined && !(isFiniteNumber(value.buttonsAt) && value.buttonsAt >= 0))
      this.error(`${path}/buttonsAt`, "Must be a non-negative number of seconds.");

    const own = this.buttons(value, path, canvas, mediaIds, menus, "this menu");
    if (!own) return;
    if (value.defaultButton !== undefined) this.buttonRef(value.defaultButton, `${path}/defaultButton`, own, "this menu");
    // Level 2: buttons that change while the menu plays. back, timeout and keys may press any of them.
    if (value.buttonSets !== undefined) this.buttonSets(value.buttonSets, `${path}/buttonSets`, canvas, mediaIds, menus, own);
    // What the remote's Back does here; may press one of this menu's own buttons.
    if (value.back !== undefined) this.action(value.back, `${path}/back`, mediaIds, menus, own);
    // Like back, a menu's keys may press one of its own buttons.
    if (value.keys !== undefined) this.keys(value.keys, `${path}/keys`, mediaIds, menus, false, own);
    if (value.prohibits !== undefined) this.prohibits(value.prohibits, `${path}/prohibits`);
    if (value.timeout !== undefined) {
      const t = value.timeout;
      const tp = `${path}/timeout`;
      if (!isObject(t)) this.error(tp, "Must be {after, action, restartOnInput?}.");
      else {
        this.unknownKeys(t, KNOWN_KEYS.timeout!, tp);
        if (!(isFiniteNumber(t.after) && t.after > 0)) this.error(`${tp}/after`, "Must be a positive number of seconds.");
        if (t.restartOnInput !== undefined && typeof t.restartOnInput !== "boolean") this.error(`${tp}/restartOnInput`, "Must be true or false.");
        // Like back, it may press one of this menu's own buttons.
        this.action(t.action, `${tp}/action`, mediaIds, menus, own);
      }
    }
  }

  private canvas(c: unknown, path: string): { width: number; height: number } | undefined {
    if (!isObject(c)) {
      this.error(path, "Required: {width, height, aspect?}.");
      return undefined;
    }
    this.unknownKeys(c, KNOWN_KEYS.canvas!, path);
    const okW = Number.isInteger(c.width) && (c.width as number) > 0;
    const okH = Number.isInteger(c.height) && (c.height as number) > 0;
    if (!okW) this.error(`${path}/width`, "Must be a positive integer.");
    if (!okH) this.error(`${path}/height`, "Must be a positive integer.");
    if (c.aspect !== undefined && parseAspect(c.aspect) === undefined)
      this.error(`${path}/aspect`, 'Must be "W:H" (e.g. "16:9") or a positive number.');
    return okW && okH ? { width: c.width as number, height: c.height as number } : undefined;
  }

  /** The buttons of a menu or overlay; returns their ids, or undefined when there are none. */
  private buttons(
    value: Record<string, unknown>,
    path: string,
    canvas: { width: number; height: number } | undefined,
    mediaIds: Map<string, number>,
    menus: Map<string, Set<string>>,
    where: string,
  ): Set<string> | undefined {
    if (!Array.isArray(value.buttons) || value.buttons.length === 0) {
      this.error(`${path}/buttons`, "Required: at least one button.");
      return undefined;
    }
    const own = new Set<string>();
    value.buttons.forEach((b, i) => {
      const bp = `${path}/buttons/${i}`;
      if (!isObject(b)) {
        this.error(bp, "Must be an object.");
        return;
      }
      this.unknownKeys(b, KNOWN_KEYS.button!, bp);
      if (!isNonEmptyString(b.id)) this.error(`${bp}/id`, "Required.");
      else if (own.has(b.id)) this.error(`${bp}/id`, `Duplicate button id "${b.id}".`);
      else own.add(b.id);

      this.rect(b.rect, `${bp}/rect`, canvas);
      if (b.altRects !== undefined) {
        if (!isObject(b.altRects)) this.error(`${bp}/altRects`, "Must be {letterbox?, panscan?}.");
        else {
          this.unknownKeys(b.altRects, ["letterbox", "panscan"], `${bp}/altRects`);
          for (const key of ["letterbox", "panscan"] as const)
            if (b.altRects[key] !== undefined) this.rect(b.altRects[key], `${bp}/altRects/${key}`, canvas);
        }
      }
      for (const key of ["label", "a11yLabel"] as const)
        if (b[key] !== undefined && typeof b[key] !== "string") this.error(`${bp}/${key}`, "Must be a string.");
      for (const key of ["showLabel", "autoAction"] as const)
        if (b[key] !== undefined && typeof b[key] !== "boolean") this.error(`${bp}/${key}`, "Must be true or false.");
      if (b.showLabel === true && !isNonEmptyString(b.label))
        this.error(`${bp}/label`, "Required when showLabel is true.");

      if (b.states !== undefined) {
        if (!isObject(b.states)) this.error(`${bp}/states`, "Must be an object.");
        else
          for (const [name, s] of Object.entries(b.states)) {
            const sp = `${bp}/states/${name}`;
            if (!["normal", "selected", "activated"].includes(name)) {
              if (!name.startsWith("x-")) this.warn(sp, `Unknown state "${name}" (ignored).`);
              continue;
            }
            if (!isObject(s)) {
              this.error(sp, "Must be an object.");
              continue;
            }
            this.unknownKeys(s, KNOWN_KEYS.state!, sp);
            if (s.image !== undefined && !isNonEmptyString(s.image)) this.error(`${sp}/image`, "Must be a path or URL.");
            if (s.rect !== undefined) this.rect(s.rect, `${sp}/rect`, canvas);
            for (const key of ["textColor", "fill"] as const)
              if (s[key] !== undefined && typeof s[key] !== "string") this.error(`${sp}/${key}`, "Must be a colour string.");
          }
      }
      this.action(b.action, `${bp}/action`, mediaIds, menus);
    });

    // nav and defaultButton can only be checked once every id in this menu is known.
    value.buttons.forEach((b, i) => {
      if (!isObject(b) || b.nav === undefined) return;
      const np = `${path}/buttons/${i}/nav`;
      if (!isObject(b.nav)) {
        this.error(np, "Must be an object of direction → button id.");
        return;
      }
      for (const [dir, target] of Object.entries(b.nav)) {
        if (!["up", "down", "left", "right"].includes(dir)) {
          if (!dir.startsWith("x-")) this.warn(`${np}/${dir}`, `Unknown direction "${dir}" (ignored).`);
        } else if (!isNonEmptyString(target) || !own.has(target))
          this.error(`${np}/${dir}`, `No button "${String(target)}" in ${where}.`);
      }
    });
    return own;
  }

  /** Level 2: a menu's button sets (spec 5.3): windows on its clock that don't overlap, each with its buttons. */
  private buttonSets(
    value: unknown,
    path: string,
    canvas: { width: number; height: number } | undefined,
    mediaIds: Map<string, number>,
    menus: Map<string, Set<string>>,
    own: Set<string>,
  ) {
    if (!Array.isArray(value) || value.length === 0) {
      this.error(path, "Must be a list of at least one button set {from, to?, buttons}.");
      return;
    }
    const windows: { from: number; to: number; i: number }[] = [];
    value.forEach((set, i) => {
      const sp = `${path}/${i}`;
      if (!isObject(set)) {
        this.error(sp, "Must be a button set {from, to?, buttons}.");
        return;
      }
      this.unknownKeys(set, KNOWN_KEYS.buttonSet!, sp);
      const okFrom = isFiniteNumber(set.from) && set.from >= 0;
      if (!okFrom) this.error(`${sp}/from`, "Must be a non-negative number of seconds.");
      const okTo = set.to === undefined || (okFrom && isFiniteNumber(set.to) && set.to > (set.from as number));
      if (!okTo) this.error(`${sp}/to`, "Must be a number of seconds after from.");
      else if (okFrom) {
        const from = set.from as number;
        const to = (set.to as number | undefined) ?? Infinity;
        const other = windows.find((w) => from < w.to && w.from < to);
        if (other) this.error(`${sp}/from`, `Overlaps button set ${other.i}; one set shows at a time.`);
        windows.push({ from, to, i });
      }
      if (set.autoNav !== undefined && typeof set.autoNav !== "boolean") this.error(`${sp}/autoNav`, "Must be true or false.");
      const ids = this.buttons(set, sp, canvas, mediaIds, menus, "this button set");
      if (ids) {
        if (set.defaultButton !== undefined) this.buttonRef(set.defaultButton, `${sp}/defaultButton`, ids, "this button set");
        for (const id of ids) own.add(id);
      }
    });
  }

  /**
   * Level 2: cues during playback (spec 6.3). Point cues {at, action}; overlays {from, to, canvas,
   * buttons}, which must not overlap each other within one list.
   */
  private cues(value: unknown, path: string, mediaIds: Map<string, number>, menus: Map<string, Set<string>>) {
    if (!Array.isArray(value)) {
      this.error(path, "Must be a list of cues.");
      return;
    }
    const windows: { from: number; to: number; i: number }[] = [];
    this.cueDepth++;
    value.forEach((cue, i) => {
      const cp = `${path}/${i}`;
      if (!isObject(cue)) this.error(cp, "Must be a point cue {at, action} or an overlay {from, to, canvas, buttons}.");
      else if (cue.at !== undefined) {
        this.unknownKeys(cue, KNOWN_KEYS.cue!, cp);
        if (!(isFiniteNumber(cue.at) && cue.at >= 0)) this.error(`${cp}/at`, "Must be a non-negative number of seconds.");
        this.action(cue.action, `${cp}/action`, mediaIds, menus);
      } else if (cue.from !== undefined || cue.to !== undefined) {
        this.unknownKeys(cue, KNOWN_KEYS.overlay!, cp);
        const okFrom = isFiniteNumber(cue.from) && cue.from >= 0;
        if (!okFrom) this.error(`${cp}/from`, "Must be a non-negative number of seconds.");
        const okTo = okFrom && isFiniteNumber(cue.to) && cue.to > (cue.from as number);
        if (!okTo) this.error(`${cp}/to`, "Must be a number of seconds after from.");
        else {
          const from = cue.from as number;
          const to = cue.to as number;
          const other = windows.find((w) => from < w.to && w.from < to);
          if (other) this.error(`${cp}/from`, `Overlaps overlay ${other.i}; one overlay shows at a time.`);
          windows.push({ from, to, i });
        }
        const canvas = this.canvas(cue.canvas, `${cp}/canvas`);
        if (cue.autoNav !== undefined && typeof cue.autoNav !== "boolean") this.error(`${cp}/autoNav`, "Must be true or false.");
        const own = this.buttons(cue, cp, canvas, mediaIds, menus, "this overlay");
        if (own && cue.defaultButton !== undefined) this.buttonRef(cue.defaultButton, `${cp}/defaultButton`, own, "this overlay");
      } else this.error(cp, "Must be a point cue {at, action} or an overlay {from, to, canvas, buttons}.");
    });
    this.cueDepth--;
  }

  private loopingMedia(value: unknown, path: string) {
    if (!isObject(value)) {
      this.error(path, "Must be {src, loopStart?}.");
      return;
    }
    this.unknownKeys(value, KNOWN_KEYS.loopingMedia!, path);
    if (!isNonEmptyString(value.src)) this.error(`${path}/src`, "Required: a path or URL.");
    if (value.loopStart !== undefined && !(isFiniteNumber(value.loopStart) && value.loopStart >= 0))
      this.error(`${path}/loopStart`, "Must be a non-negative number of seconds.");
  }

  private rect(value: unknown, path: string, canvas?: { width: number; height: number }) {
    if (!Array.isArray(value) || value.length !== 4 || !value.every(isFiniteNumber)) {
      this.error(path, "Must be [x, y, width, height].");
      return;
    }
    const [x, y, w, h] = value as number[] as [number, number, number, number];
    if (w < 0 || h < 0) this.error(path, "Width and height must not be negative.");
    else if (canvas && (x < 0 || y < 0 || x + w > canvas.width || y + h > canvas.height))
      this.warn(path, "Extends outside the canvas.");
  }

  private playItem(value: Record<string, unknown>, path: string, mediaIds: Map<string, number>, menus: Map<string, Set<string>>) {
    if (!isNonEmptyString(value.media) || !mediaIds.has(value.media)) {
      this.error(`${path}/media`, `No media "${String(value.media)}".`);
      return;
    }
    if (value.chapter !== undefined) {
      const count = mediaIds.get(value.media)!;
      if (!Number.isInteger(value.chapter) || (value.chapter as number) < 1 || (value.chapter as number) > count)
        this.error(`${path}/chapter`, `Media "${value.media}" has ${count} chapter(s); chapter numbers start at 1.`);
    }
    if (value.start !== undefined && !(isFiniteNumber(value.start) && value.start >= 0))
      this.error(`${path}/start`, "Must be a non-negative number of seconds.");
    if (value.end !== undefined) {
      if (!(isFiniteNumber(value.end) && value.end > 0)) this.error(`${path}/end`, "Must be a positive number of seconds.");
      else if (isFiniteNumber(value.start) && value.end <= value.start) this.error(`${path}/end`, "Must be after start.");
    }
    if (value.audio !== undefined) this.varOrNumber(value.audio, `${path}/audio`, 1);
    if (value.subtitle !== undefined) this.varOrNumber(value.subtitle, `${path}/subtitle`, 0);
    if (value.angle !== undefined) {
      this.varOrNumber(value.angle, `${path}/angle`, 1);
      const count = this.angleCounts.get(value.media) ?? 1;
      if (Number.isInteger(value.angle) && (value.angle as number) > count)
        this.error(`${path}/angle`, `Media "${value.media}" has ${count} angle(s).`);
    }
    if (value.cues !== undefined) this.cues(value.cues, `${path}/cues`, mediaIds, menus);
    if (value.keys !== undefined) this.keys(value.keys, `${path}/keys`, mediaIds, menus, true);
    if (value.prohibits !== undefined) this.prohibits(value.prohibits, `${path}/prohibits`);
    if (value.prohibitsAt !== undefined) this.prohibitsAt(value.prohibitsAt, `${path}/prohibitsAt`);
    if (value.holds !== undefined) this.holds(value.holds, `${path}/holds`);
  }

  /** Level 2: what a source forbids during parts of a title (spec 6.6): windows {from, to?, ops}, which may overlap. */
  private prohibitsAt(value: unknown, path: string) {
    if (!Array.isArray(value)) {
      this.error(path, 'Must be a list of windows {from, to?, ops}, e.g. [{"from": 0.5, "ops": ["next"]}].');
      return;
    }
    value.forEach((w, i) => {
      const wp = `${path}/${i}`;
      if (!isObject(w)) {
        this.error(wp, "Must be a window {from, to?, ops}.");
        return;
      }
      this.unknownKeys(w, KNOWN_KEYS.prohibitWindow!, wp);
      const okFrom = isFiniteNumber(w.from) && w.from >= 0;
      if (!okFrom) this.error(`${wp}/from`, "Must be a non-negative number of seconds.");
      if (w.to !== undefined && !(okFrom && isFiniteNumber(w.to) && w.to > (w.from as number))) this.error(`${wp}/to`, "Must be a number of seconds after from.");
      this.prohibits(w.ops, `${wp}/ops`);
    });
  }

  /** Level 2: frames held for a time or until the viewer goes on (spec 6.3). */
  private holds(value: unknown, path: string) {
    if (!Array.isArray(value)) {
      this.error(path, 'Must be a list of holds {at, for}, e.g. [{"at": 12.5, "for": "input"}].');
      return;
    }
    value.forEach((h, i) => {
      const hp = `${path}/${i}`;
      if (!isObject(h)) {
        this.error(hp, "Must be a hold {at, for}.");
        return;
      }
      this.unknownKeys(h, KNOWN_KEYS.hold!, hp);
      if (!(isFiniteNumber(h.at) && h.at >= 0)) this.error(`${hp}/at`, "Must be a non-negative number of seconds.");
      if (!(h.for === "input" || (isFiniteNumber(h.for) && h.for > 0))) this.error(`${hp}/for`, 'Must be a positive number of seconds, or "input".');
    });
  }

  /**
   * Level 2: a table of menu key → action (spec 6.6). `duringPlayback`: the keys may run while a title
   * plays, where they act like a cue's action.
   */
  private keys(value: unknown, path: string, mediaIds: Map<string, number>, menus: Map<string, Set<string>>, duringPlayback: boolean, ownButtons?: Set<string>) {
    if (!isObject(value)) {
      this.error(path, `Must be an object of key → action (${MENU_KEYS.join(", ")}).`);
      return;
    }
    if (duringPlayback) this.cueDepth++;
    for (const [key, action] of Object.entries(value)) {
      if (!(MENU_KEYS as readonly string[]).includes(key)) {
        if (!key.startsWith("x-")) this.warn(`${path}/${key}`, `Unknown key "${key}" (ignored). Known: ${MENU_KEYS.join(", ")}.`);
      } else this.action(action, `${path}/${key}`, mediaIds, menus, ownButtons);
    }
    if (duringPlayback) this.cueDepth--;
  }

  /** Level 2: what a source forbids, as information (spec 6.6); unknown values are allowed but warned about. */
  private prohibits(value: unknown, path: string) {
    if (!Array.isArray(value) || !value.every(isNonEmptyString)) {
      this.error(path, "Must be a list of prohibitions, e.g. [\"fastForward\", \"menuKey\"].");
      return;
    }
    value.forEach((p, i) => {
      if (!(PROHIBITIONS as readonly string[]).includes(p) && !p.startsWith("x-"))
        this.warn(`${path}/${i}`, `Unknown prohibition "${p}" (players ignore it). Known: ${PROHIBITIONS.join(", ")}.`);
    });
  }

  /** `ownButtons`: set when checking a menu's `back`, the only place `activate` may appear. */
  action(value: unknown, path: string, mediaIds: Map<string, number>, menus: Map<string, Set<string>>, ownButtons?: Set<string>) {
    if (!isObject(value)) {
      this.error(path, "Required action object with a type.");
      return;
    }
    const type = value.type;
    if (typeof type !== "string" || !(type in ACTION_KEYS)) {
      this.error(`${path}/type`, `Unknown action type "${String(type)}". Expected ${Object.keys(ACTION_KEYS).join(", ")}.`);
      return;
    }
    this.unknownKeys(value, ACTION_KEYS[type]!, path);
    switch (type) {
      case "play":
        this.playItem(value, path, mediaIds, menus);
        break;
      case "playAll":
        if (!Array.isArray(value.items) || value.items.length === 0) this.error(`${path}/items`, "Required: at least one item.");
        else
          value.items.forEach((item, i) => {
            if (!isObject(item)) this.error(`${path}/items/${i}`, "Must be {media, chapter?, start?}.");
            else this.playItem(item, `${path}/items/${i}`, mediaIds, menus);
          });
        if (value.order !== undefined && !["inOrder", "shuffle", "random"].includes(value.order as string))
          this.error(`${path}/order`, 'Must be "inOrder", "shuffle" or "random".');
        if (value.count !== undefined) {
          if (!(Number.isInteger(value.count) && (value.count as number) >= 1)) this.error(`${path}/count`, "Must be a whole number of items, at least 1.");
          else if (value.order !== "random") this.error(`${path}/count`, 'Only with order "random".');
        }
        break;
      case "set": {
        const name = value.var;
        const target = this.typeOfVar(name);
        if (!target) this.error(`${path}/var`, `No variable "${String(name)}" in /vars or player state.`);
        else if (typeof name === "string" && name.startsWith("@") && !PLAYER_VARS[name]!.writable)
          this.error(`${path}/var`, `"${name}" is read-only player state.`);
        const op = value.op ?? "set";
        if (!SET_OPS.includes(op as never)) this.error(`${path}/op`, `Must be one of ${SET_OPS.join(", ")}.`);
        const given = this.value(value.value, `${path}/value`);
        if (op === "set") {
          if (target && given && target !== given) this.error(`${path}/value`, `"${String(name)}" holds ${target === "number" ? "an integer" : "text"}.`);
        } else if (target === "string" || given === "string") this.error(`${path}/op`, `"${String(op)}" works on integers only.`);
        if (value.then !== undefined) this.action(value.then, `${path}/then`, mediaIds, menus, ownButtons);
        break;
      }
      case "if":
        this.test(value.test, `${path}/test`);
        this.action(value.then, `${path}/then`, mediaIds, menus, ownButtons);
        if (value.else !== undefined) this.action(value.else, `${path}/else`, mediaIds, menus, ownButtons);
        break;
      case "do":
        if (!Array.isArray(value.steps) || value.steps.length === 0) this.error(`${path}/steps`, "Required: at least one action.");
        else value.steps.forEach((st, i) => this.action(st, `${path}/steps/${i}`, mediaIds, menus, ownButtons));
        break;
      case "resume":
        if (value.otherwise !== undefined) {
          if (isObject(value.otherwise) && value.otherwise.type === "resume")
            this.error(`${path}/otherwise/type`, "Must not be another resume: there would still be nothing to resume.");
          else this.action(value.otherwise, `${path}/otherwise`, mediaIds, menus, ownButtons);
        }
        break;
      case "run":
        if (!isNonEmptyString(value.action) || !this.named.has(value.action)) this.error(`${path}/action`, `No action "${String(value.action)}" in /actions.`);
        else if (this.cueDepth > 0 && !this.replacing && this.playsWithAfter.has(value.action))
          this.error(`${path}/action`, `Action "${value.action}" starts playback with after; playback started during playback returns to it, so after is not allowed here.`);
        break;
      case "saveResume":
        // The resumed play item, with its own end, cues and holds: checked as any play item, and its
        // media is the saved one.
        if (value.item !== undefined) {
          if (value.media !== undefined) this.error(`${path}/item`, "Give media or item, not both.");
          else if (!isObject(value.item)) this.error(`${path}/item`, "Must be a play item {media, end?, cues?, ...}.");
          else {
            this.unknownKeys(value.item, ACTION_KEYS.play!.filter((k) => k !== "type" && k !== "after"), `${path}/item`);
            this.playItem(value.item, `${path}/item`, mediaIds, menus);
          }
        }
        if (value.media !== undefined) {
          if (!isNonEmptyString(value.media) || !mediaIds.has(value.media)) this.error(`${path}/media`, `No media "${String(value.media)}".`);
          else if (value.chapter !== undefined) {
            const count = mediaIds.get(value.media)!;
            if (!Number.isInteger(value.chapter) || (value.chapter as number) < 1 || (value.chapter as number) > count)
              this.error(`${path}/chapter`, `Media "${value.media}" has ${count} chapter(s); chapter numbers start at 1.`);
          }
        } else if (value.chapter !== undefined && !(Number.isInteger(value.chapter) && (value.chapter as number) >= 1))
          this.error(`${path}/chapter`, "Must be a chapter number (from 1).");
        if (value.at !== undefined && !(isFiniteNumber(value.at) && value.at >= 0)) this.error(`${path}/at`, "Must be a non-negative number of seconds.");
        if (value.at !== undefined && value.chapter !== undefined) this.error(`${path}/at`, "Give at or chapter, not both.");
        // What follows the saved media when resumed: only with media (without it, "here" brings its own).
        if ((value.queue !== undefined || value.after !== undefined) && value.media === undefined && value.item === undefined)
          this.error(`${path}/${value.queue !== undefined ? "queue" : "after"}`, "Only with media: saving \"here\" keeps what follows the playback in progress.");
        if (value.queue !== undefined) {
          if (!Array.isArray(value.queue) || value.queue.length === 0) this.error(`${path}/queue`, "Must be a list of at least one item {media, chapter?, start?}.");
          else
            value.queue.forEach((item, i) => {
              if (!isObject(item)) this.error(`${path}/queue/${i}`, "Must be {media, chapter?, start?}.");
              else this.playItem(item, `${path}/queue/${i}`, mediaIds, menus);
            });
        }
        if (value.after !== undefined) {
          // Runs when the resumed playback completes, as a play's after: it must not start playback.
          const after = this.resolveRun(value.after);
          if (isObject(after) && (after.type === "play" || after.type === "playAll"))
            this.error(`${path}/after`, "After-playback action must not start another playback; use queue instead.");
          else {
            const depth = this.cueDepth;
            this.cueDepth = 0;
            this.action(value.after, `${path}/after`, mediaIds, menus);
            this.cueDepth = depth;
          }
        }
        break;
      case "seek":
        if (this.cueDepth === 0 && this.namedDepth === 0)
          this.error(`${path}/type`, '"seek" moves the playback in progress; it only works in cues, overlays and keys that run during playback.');
        if (value.at !== undefined && value.chapter !== undefined) this.error(`${path}/at`, "Give at or chapter, not both.");
        else if (value.at === undefined && value.chapter === undefined) this.error(path, "Needs at (seconds) or chapter.");
        else if (value.at !== undefined && !(isFiniteNumber(value.at) && value.at >= 0)) this.error(`${path}/at`, "Must be a non-negative number of seconds.");
        else if (value.chapter !== undefined && !(Number.isInteger(value.chapter) && (value.chapter as number) >= 1))
          this.error(`${path}/chapter`, "Must be a chapter number (from 1).");
        break;
      case "requestParental":
        if (!(Number.isInteger(value.level) && (value.level as number) >= 1 && (value.level as number) <= 8))
          this.error(`${path}/level`, "Must be a parental level from 1 to 8.");
        this.action(value.then, `${path}/then`, mediaIds, menus, ownButtons);
        if (value.else !== undefined) this.action(value.else, `${path}/else`, mediaIds, menus, ownButtons);
        break;
      case "timer":
        if (!(isFiniteNumber(value.after) && value.after > 0)) this.error(`${path}/after`, "Must be a positive number of seconds.");
        // It runs wherever the viewer is when the time is up, playback included: as a cue's action.
        this.cueDepth++;
        this.action(value.action, `${path}/action`, mediaIds, menus);
        this.cueDepth--;
        break;
      case "activate":
        if (!ownButtons) this.error(`${path}/type`, `"activate" is only allowed in a menu's back, timeout or keys.`);
        else if (!isNonEmptyString(value.button) || !ownButtons.has(value.button))
          this.error(`${path}/button`, `No button "${String(value.button)}" in this menu.`);
        break;
      case "goto": {
        if (!isNonEmptyString(value.menu) || !menus.has(value.menu)) {
          this.error(`${path}/menu`, `No menu "${String(value.menu)}".`);
          break;
        }
        if (value.button !== undefined) this.buttonRef(value.button, `${path}/button`, menus.get(value.menu)!, `menu "${value.menu}"`);
        if (value.transition !== undefined && !isNonEmptyString(value.transition))
          this.error(`${path}/transition`, "Must be a path or URL to a clip.");
        if (value.history !== undefined && value.history !== "push" && value.history !== "replace")
          this.error(`${path}/history`, 'Must be "push" or "replace".');
        break;
      }
    }
    if ((type === "play" || type === "playAll") && value.after !== undefined && this.cueDepth > 0 && !this.replacing) {
      this.error(`${path}/after`, "Playback started during playback returns to it when done; after is not allowed here.");
    } else if ((type === "play" || type === "playAll") && value.after !== undefined) {
      const after: unknown = this.resolveRun(value.after);
      const otherwise: unknown = isObject(after) && after.type === "resume" ? this.resolveRun(after.otherwise) : undefined;
      if (isObject(after) && (after.type === "play" || after.type === "playAll"))
        this.error(`${path}/after`, "After-playback action must not start another playback; use playAll instead.");
      else if (isObject(otherwise) && (otherwise.type === "play" || otherwise.type === "playAll"))
        this.error(`${path}/after/otherwise`, "After-playback action must not start another playback; use playAll instead.");
      else this.action(value.after, `${path}/after`, mediaIds, menus);
    }
  }

  private fallback(value: unknown, mediaIds: Map<string, number>, menus: Map<string, Set<string>>) {
    if (!Array.isArray(value) || value.length === 0) {
      this.error("/fallback", "Required: at least one group. Every manifest carries a plain list for basic players.");
      return;
    }
    let visibleItems = 0;
    value.forEach((g, i) => {
      const gp = `/fallback/${i}`;
      if (!isObject(g)) {
        this.error(gp, "Must be {label, items}.");
        return;
      }
      this.unknownKeys(g, KNOWN_KEYS.fallbackGroup!, gp);
      if (!isNonEmptyString(g.label)) this.error(`${gp}/label`, "Required.");
      if (g.hidden !== undefined && typeof g.hidden !== "boolean") this.error(`${gp}/hidden`, "Must be true or false.");
      if (!Array.isArray(g.items) || g.items.length === 0) {
        this.error(`${gp}/items`, "Required: at least one item.");
        return;
      }
      g.items.forEach((item, j) => {
        const ip = `${gp}/items/${j}`;
        if (!isObject(item)) {
          this.error(ip, "Must be {label, action}.");
          return;
        }
        this.unknownKeys(item, KNOWN_KEYS.fallbackItem!, ip);
        if (!isNonEmptyString(item.label)) this.error(`${ip}/label`, "Required.");
        this.action(item.action, `${ip}/action`, mediaIds, menus);
        const t = isObject(item.action) ? item.action.type : undefined;
        if (t === "goto" || t === "back" || t === "resume" || t === "set" || t === "activate" || t === "if" || t === "do" || t === "run" || t === "saveResume" || t === "clearResume" || t === "seek" || t === "timer" || t === "cancelTimer" || t === "requestParental" || t === "exit")
          this.error(`${ip}/action/type`, `Fallback items must play something ("${String(t)}" needs a menu-capable player).`);
      });
      if (g.hidden !== true) visibleItems += g.items.length;
    });
    if (visibleItems === 0)
      this.error("/fallback", "At least one fallback group must be visible (not hidden).");
  }
}

/**
 * Visit an action and the actions it runs in the same place (set's then, if's branches, do's steps,
 * resume's otherwise); not after-actions or cues, which run later, at playback's end or during it.
 */
function sameContext(action: unknown, visit: (a: Record<string, unknown>) => void) {
  if (!isObject(action)) return;
  visit(action);
  for (const key of ["then", "else", "otherwise"]) sameContext(action[key], visit);
  if (Array.isArray(action.steps)) for (const st of action.steps) sameContext(st, visit);
}

/** Parse "16:9", "4:3", "1.85" or a number into width/height ratio. */
export function parseAspect(value: unknown): number | undefined {
  if (isFiniteNumber(value)) return value > 0 ? value : undefined;
  if (typeof value !== "string") return undefined;
  const m = /^\s*(\d+(?:\.\d+)?)\s*[:/]\s*(\d+(?:\.\d+)?)\s*$/.exec(value);
  if (m) {
    const r = Number(m[1]) / Number(m[2]);
    return r > 0 && Number.isFinite(r) ? r : undefined;
  }
  const n = Number(value);
  return Number.isFinite(n) && n > 0 ? n : undefined;
}

/** Display aspect of a menu canvas (defaults to square pixels). */
export function canvasAspect(menu: Pick<Menu, "canvas">): number {
  return parseAspect(menu.canvas.aspect) ?? menu.canvas.width / menu.canvas.height;
}

export type { PlayItem };
