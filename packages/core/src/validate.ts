import { FORMAT_ID, FORMAT_VERSION } from "./constants.js";
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
  manifest: ["format", "version", "title", "source", "media", "menus", "rootMenu", "firstPlay", "fallback", "$schema"],
  title: ["name", "year", "ids"],
  source: ["kind", "generator"],
  media: ["ref", "label", "kind", "duration", "chapters"],
  chapter: ["time", "label", "thumbnail"],
  menu: ["canvas", "background", "label", "defaultButton", "autoNav", "buttons", "buttonsAt"],
  canvas: ["width", "height", "aspect"],
  background: ["image", "color", "video", "audio"],
  loopingMedia: ["src", "loopStart"],
  button: ["id", "rect", "label", "a11yLabel", "showLabel", "states", "nav", "autoAction", "action"],
  state: ["image", "rect", "textColor", "fill"],
  fallbackGroup: ["label", "hidden", "items"],
  fallbackItem: ["label", "action"],
};

const ACTION_KEYS: Record<string, readonly string[]> = {
  play: ["type", "media", "chapter", "start", "after"],
  playAll: ["type", "items", "after"],
  goto: ["type", "menu", "button", "transition"],
  back: ["type"],
  resume: ["type"],
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

    if (data.format !== FORMAT_ID) {
      this.error("/format", `Must be "${FORMAT_ID}".`);
    }
    this.version(data.version);
    this.title(data.title);
    if (data.source !== undefined) this.source(data.source);

    const mediaIds = this.media(data.media);
    const menus = this.menus(data.menus, mediaIds);

    if (data.rootMenu !== undefined) {
      if (!isNonEmptyString(data.rootMenu)) this.error("/rootMenu", "Must be a menu id.");
      else if (!menus.has(data.rootMenu)) this.error("/rootMenu", `No menu "${data.rootMenu}".`);
    } else if (menus.size > 0) {
      this.error("/rootMenu", "Required when the manifest has menus.");
    }

    if (data.firstPlay !== undefined) this.action(data.firstPlay, "/firstPlay", mediaIds, menus);

    this.fallback(data.fallback, mediaIds, menus);
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
      const ref = m.ref;
      if (isObject(ref)) {
        if (!isNonEmptyString(ref.scheme) || !isNonEmptyString(ref.id))
          this.error(`${path}/ref`, "Object refs need non-empty scheme and id.");
      } else if (!isNonEmptyString(ref)) {
        this.error(`${path}/ref`, "Required: a relative path, URL, or {scheme, id}.");
      }
      if (m.label !== undefined && typeof m.label !== "string") this.error(`${path}/label`, "Must be a string.");
      if (m.kind !== undefined && typeof m.kind !== "string") this.error(`${path}/kind`, "Must be a string.");
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
    return ids;
  }

  /** Returns menu id → set of its button ids. */
  private menus(value: unknown, mediaIds: Map<string, number>): Map<string, Set<string>> {
    const menus = new Map<string, Set<string>>();
    if (value === undefined) return menus;
    if (!isObject(value)) {
      this.error("/menus", "Must be an object of menus.");
      return menus;
    }
    // First pass: collect button ids so goto targets can be checked in any order.
    for (const [id, m] of Object.entries(value)) {
      const buttons = new Set<string>();
      if (isObject(m) && Array.isArray(m.buttons))
        for (const b of m.buttons) if (isObject(b) && isNonEmptyString(b.id)) buttons.add(b.id);
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

    let canvas: { width: number; height: number } | undefined;
    const c = value.canvas;
    if (!isObject(c)) this.error(`${path}/canvas`, "Required: {width, height, aspect?}.");
    else {
      this.unknownKeys(c, KNOWN_KEYS.canvas!, `${path}/canvas`);
      const okW = Number.isInteger(c.width) && (c.width as number) > 0;
      const okH = Number.isInteger(c.height) && (c.height as number) > 0;
      if (!okW) this.error(`${path}/canvas/width`, "Must be a positive integer.");
      if (!okH) this.error(`${path}/canvas/height`, "Must be a positive integer.");
      if (okW && okH) canvas = { width: c.width as number, height: c.height as number };
      if (c.aspect !== undefined && parseAspect(c.aspect) === undefined)
        this.error(`${path}/canvas/aspect`, 'Must be "W:H" (e.g. "16:9") or a positive number.');
    }

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

    if (!Array.isArray(value.buttons) || value.buttons.length === 0) {
      this.error(`${path}/buttons`, "Required: at least one button.");
      return;
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
          this.error(`${np}/${dir}`, `No button "${String(target)}" in this menu.`);
      }
    });
    if (value.defaultButton !== undefined && !(isNonEmptyString(value.defaultButton) && own.has(value.defaultButton)))
      this.error(`${path}/defaultButton`, `No button "${String(value.defaultButton)}" in this menu.`);
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

  private playItem(value: Record<string, unknown>, path: string, mediaIds: Map<string, number>) {
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
  }

  action(value: unknown, path: string, mediaIds: Map<string, number>, menus: Map<string, Set<string>>) {
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
        this.playItem(value, path, mediaIds);
        break;
      case "playAll":
        if (!Array.isArray(value.items) || value.items.length === 0) this.error(`${path}/items`, "Required: at least one item.");
        else
          value.items.forEach((item, i) => {
            if (!isObject(item)) this.error(`${path}/items/${i}`, "Must be {media, chapter?, start?}.");
            else this.playItem(item, `${path}/items/${i}`, mediaIds);
          });
        break;
      case "goto": {
        if (!isNonEmptyString(value.menu) || !menus.has(value.menu)) {
          this.error(`${path}/menu`, `No menu "${String(value.menu)}".`);
          break;
        }
        if (value.button !== undefined && !(isNonEmptyString(value.button) && menus.get(value.menu)!.has(value.button)))
          this.error(`${path}/button`, `No button "${String(value.button)}" in menu "${value.menu}".`);
        if (value.transition !== undefined && !isNonEmptyString(value.transition))
          this.error(`${path}/transition`, "Must be a path or URL to a clip.");
        break;
      }
    }
    if ((type === "play" || type === "playAll") && value.after !== undefined) {
      const after: unknown = value.after;
      if (isObject(after) && (after.type === "play" || after.type === "playAll"))
        this.error(`${path}/after`, "After-playback action must not start another playback; use playAll instead.");
      else this.action(after, `${path}/after`, mediaIds, menus);
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
        if (t === "goto" || t === "back" || t === "resume")
          this.error(`${ip}/action/type`, `Fallback items must play something ("${String(t)}" needs a menu-capable player).`);
      });
      if (g.hidden !== true) visibleItems += g.items.length;
    });
    if (visibleItems === 0)
      this.error("/fallback", "At least one fallback group must be visible (not hidden).");
  }
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
