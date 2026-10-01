/**
 * Open Video Menu manifest v0.1 (draft) — TypeScript model.
 * The normative definition is spec/openvideomenu-v0.1.md and spec/schema/manifest.schema.json.
 *
 * Unknown properties are allowed everywhere (compatibility charter rule 3); vendor
 * extensions use keys that start with "x-".
 */

export interface Manifest {
  format: string;
  version: string;
  title: Title;
  source?: Source;
  media: Record<string, Media>;
  menus?: Record<string, Menu>;
  rootMenu?: string;
  firstPlay?: Action;
  fallback: FallbackGroup[];
  /**
   * Level 2: named variables for the viewing session (the data-only stand-in for DVD registers), with
   * their starting values: an integer or text, and a variable keeps that type. Set by "set", read by
   * conditions, play's audio/subtitle and focus. Player state is read as "@name" variables (logic.ts).
   */
  vars?: Record<string, Scalar>;
  /**
   * Level 2: named actions, run from anywhere with {type: "run", action: name}, so logic many buttons
   * share (a disc's PGC entry, a dispatcher, an episode's end) is written once (spec §6.4).
   */
  actions?: Record<string, Action>;
  /**
   * Level 2: how long the resume point lasts (spec §6.5). "untilEnd" (default): until that playback
   * completes. "untilUsed": like a disc's, until a resume uses it or it's replaced or cleared.
   */
  resumePoint?: "untilEnd" | "untilUsed";
  /** Level 2: what the remote's menu keys do, in menus and during playback (spec §6.6). */
  keys?: MenuKeys;
  [extension: `x-${string}`]: unknown;
}

/**
 * Level 2: the remote's menu keys (spec §6.6): Menu (a disc's root menu), Title (its title menu), and
 * the calls a disc can make to its audio, subtitle, angle and chapter menus.
 */
export type MenuKey = "menu" | "title" | "audioMenu" | "subtitleMenu" | "angleMenu" | "chapterMenu";

/** Level 2: an action per menu key; a key without one does the player's default (spec §6.6). */
export type MenuKeys = Partial<Record<MenuKey, Action>>;

/**
 * Level 2: what a source forbids the viewer, as information (spec §6.6): players decide whether to
 * honour it. Known values are listed in PROHIBITIONS; others are allowed.
 */
export type Prohibitions = string[];

export interface Title {
  name: string;
  year?: number;
  ids?: { imdb?: string; tmdb?: string; tvdb?: string; [other: string]: string | undefined };
}

export interface Source {
  /** Where the menu structure came from, e.g. "dvd", "bluray", "generated", "authored". */
  kind: string;
  /** Tool (and version) that wrote the manifest. */
  generator?: string;
}

/** Level 2: a range of a media entry that has a ref; `end` is required on every segment but the last. */
export interface Segment {
  media: string;
  /** Seconds; default 0. */
  start?: number;
  end?: number;
}

/** A relative path or URL string, or an opaque reference the host player resolves. */
export type MediaRef = string | { scheme: string; id: string };

export type MediaKind =
  | "feature"
  | "episode"
  | "trailer"
  | "featurette"
  | "behindthescenes"
  | "deleted"
  | "interview"
  | "scene"
  | "short"
  | "other";

export interface Media {
  /** The file (or server item). Absent when the media is made of `segments` (Level 2). */
  ref?: MediaRef;
  /** Level 2: pieces of other media, played as one title with its own timeline (spec §4.2). */
  segments?: Segment[];
  label?: string;
  kind?: MediaKind | string;
  /** Seconds. */
  duration?: number;
  chapters?: Chapter[];
  /** Level 2: the other angles as files; ref is angle 1, angles[0] angle 2, … (spec §4.1). */
  angles?: MediaRef[];
  /** Level 2: the file's first N video tracks are angles 1..N (spec §4.1). */
  angleTracks?: number;
  /** Level 2: what happens during playback whenever this media plays (spec §6.3). */
  cues?: Cue[];
  /** Level 2: what the menu keys do while this media plays, over the manifest's (spec §6.6). */
  keys?: MenuKeys;
  /** Level 2: what the source forbids while this media plays, as information (spec §6.6). */
  prohibits?: Prohibitions;
}

export interface Chapter {
  /** Start time in seconds from the beginning of the media. */
  time: number;
  label?: string;
  thumbnail?: string;
}

export interface Canvas {
  /** Storage size in pixels; button rects use this coordinate space. */
  width: number;
  height: number;
  /**
   * Display aspect ratio, e.g. "16:9", "4:3" or a number. Defaults to width/height (square pixels).
   * DVD menus are 720x480/576 stored and displayed at 4:3 or 16:9.
   */
  aspect?: string | number;
}

export interface Background {
  /** Still image. With `video`, it is the poster shown while loading and by players without motion support. */
  image?: string;
  /** CSS-style colour used behind/without the image. */
  color?: string;
  /** Motion background: plays once from 0, then loops from `loopStart`. May carry its own audio. */
  video?: LoopingMedia;
  /** Menu audio, looping the same way. */
  audio?: LoopingMedia;
}

export interface LoopingMedia {
  src: string;
  /** Seconds; after reaching the end, playback continues from here. Default 0 (loop the whole clip). */
  loopStart?: number;
}

/** [x, y, width, height] in canvas pixels. */
export type Rect = [number, number, number, number];

export interface ButtonState {
  /** Overlay image drawn over the button rect (or `rect` when given). PNG with alpha recommended. */
  image?: string;
  rect?: Rect;
  /** Text colour when the player draws the label (button.showLabel). */
  textColor?: string;
  /** Fill colour behind a drawn label. */
  fill?: string;
}

export type Direction = "up" | "down" | "left" | "right";

export interface Button {
  id: string;
  rect: Rect;
  /** Human-readable name; used for accessibility, fallback lists and when showLabel is true. */
  label?: string;
  /** Screen-reader text when it should differ from label. */
  a11yLabel?: string;
  /** When true the player draws `label` itself; otherwise the label is part of the background art. */
  showLabel?: boolean;
  states?: { normal?: ButtonState; selected?: ButtonState; activated?: ButtonState };
  nav?: Partial<Record<Direction, string>>;
  /** Activate as soon as the button is selected (DVD auto-action buttons). */
  autoAction?: boolean;
  action: Action;
}

export interface Menu {
  canvas: Canvas;
  background?: Background;
  label?: string;
  /** The button focused on entry: an id, or a variable holding one (Level 2). */
  defaultButton?: string | { var: string };
  /** When true, a missing nav direction moves to the nearest button in that direction. */
  autoNav?: boolean;
  buttons: Button[];
  /**
   * Seconds into the menu's intro before buttons appear and respond (DVD menus often animate in).
   * Measured on the background video when there is one. Any key skips straight to this point.
   */
  buttonsAt?: number;
  /**
   * What the remote's Back key does on this menu, like a DVD's "Go Up" target. Any action,
   * including `activate` (press one of this menu's buttons, e.g. its on-screen "Back"). Runs as a
   * step up: gotos don't add to history, and a target already in history unwinds it. Absent: Back
   * returns through history.
   */
  back?: Action;
  /**
   * Level 2: what happens when the menu is left alone, like a DVD menu whose video or still time runs
   * out. Counted from when the menu appears (its intro included).
   */
  timeout?: MenuTimeout;
  /** Level 2: what the menu keys do on this menu, over the manifest's (spec §6.6). */
  keys?: MenuKeys;
  /** Level 2: what the source forbids on this menu, as information (spec §6.6). */
  prohibits?: Prohibitions;
  [extension: `x-${string}`]: unknown;
}

/**
 * Level 2 (spec §6.3): something that happens during playback. A point cue runs its action when
 * playback passes `at`; an overlay shows buttons over the video from `from` to `to`.
 */
export type Cue = PointCue | Overlay;

export interface PointCue {
  /** Seconds into the media. */
  at: number;
  action: Action;
}

export interface Overlay {
  /** Seconds into the media: the buttons show from `from` until `to`. */
  from: number;
  to: number;
  canvas: Canvas;
  buttons: Button[];
  defaultButton?: string | { var: string };
  autoNav?: boolean;
}

export interface MenuTimeout {
  /** Seconds, from when the menu appears (or from the last key press, with restartOnInput). */
  after: number;
  /** Runs when the time is up, as a step up like a menu's back: no history added; a goto to a menu in history unwinds it. */
  action: Action;
  /** Whether a key press or click restarts the countdown. Default true. */
  restartOnInput?: boolean;
}

/** A number, or the current value of a variable. */
export type VarOrNumber = number | { var: string };

/** A variable's value: an integer or text. */
export type Scalar = number | string;

/** A literal value, or the current value of a variable (or "@" player state). */
export type Value = Scalar | { var: string };

/**
 * Level 2: a condition. One comparison of a variable with a value (eq/ne for any value; lt/le/gt/ge
 * for integers; has = any of the value's bits set), or all/any/not of other tests.
 */
export type Test =
  | { var: string; eq?: Value; ne?: Value; lt?: Value; le?: Value; gt?: Value; ge?: Value; has?: Value }
  | { all: Test[] }
  | { any: Test[] }
  | { not: Test };

/** How set changes a variable: replace it (default), arithmetic, bitwise, or a random 1..value. */
export type SetOp = "set" | "add" | "sub" | "mul" | "div" | "mod" | "and" | "or" | "xor" | "random";

export interface PlayItem {
  media: string;
  /** 1-based chapter number within the media. */
  chapter?: number;
  /** Start offset in seconds (ignored when chapter is given). */
  start?: number;
  /** Level 2: audio track to play, 1-based among the media's audio tracks. Default: the player's choice. */
  audio?: VarOrNumber;
  /** Level 2: subtitle track, 1-based among the media's subtitle tracks; 0 = off. Default: the player's choice. */
  subtitle?: VarOrNumber;
  /** Level 2: angle of media with angles, 1-based. Default: @angle. */
  angle?: VarOrNumber;
  /** Level 2: stop here (seconds), as if the item ended (spec §6.3). */
  end?: number;
  /** Level 2: cues for this way of playing the media, beside the media's own (spec §6.3). */
  cues?: Cue[];
  /** Level 2: what the menu keys do while this item plays, over the media's and the manifest's (spec §6.6). */
  keys?: MenuKeys;
  /** Level 2: what the source forbids while this item plays, beside the media's, as information (spec §6.6). */
  prohibits?: Prohibitions;
}

export type Action =
  | ({ type: "play"; after?: Action } & PlayItem)
  | { type: "playAll"; items: PlayItem[]; after?: Action }
  | {
      type: "goto";
      menu: string;
      /** The button to focus: an id, or a variable holding one (Level 2). */
      button?: string | { var: string };
      /** Clip played before the menu opens (skippable). */
      transition?: string;
      /** "replace" = don't add this menu to Back's history (page turns); default "push". */
      history?: "push" | "replace";
    }
  | { type: "back" }
  /** Continue the last stopped playback; when there's nothing to resume, run `otherwise` (not another resume). */
  | { type: "resume"; otherwise?: Action }
  /** Press a button of the current menu (its activated state shows, its action runs). Only in a menu's `back`. */
  | { type: "activate"; button: string }
  /**
   * Level 2: change a variable, then (optionally) run another action — e.g. choose a soundtrack and
   * return to the menu. op defaults to "set"; "@audio", "@subtitle", "@angle" and "@focus" can be set.
   */
  | { type: "set"; var: string; value: Value; op?: SetOp; then?: Action }
  /** Level 2: run then or (optional) else, depending on a condition. */
  | { type: "if"; test: Test; then: Action; else?: Action }
  /** Level 2: run actions in order, stopping after the first that navigates (opens a menu, plays, leaves). */
  | { type: "do"; steps: Action[] }
  /** Level 2: run a named action from the manifest's `actions`, as if it were written here. */
  | { type: "run"; action: string }
  /**
   * Level 2: save the resume point (spec §6.5): `media` from `chapter` or `at` seconds; without
   * `media`, the playback in progress, at `at` (default: the cue's time).
   */
  | { type: "saveResume"; media?: string; at?: number; chapter?: number }
  /** Level 2: forget the resume point. */
  | { type: "clearResume" };

export type ActionType = Action["type"];

export interface FallbackGroup {
  label: string;
  /** Hidden groups (e.g. easter eggs) are omitted by Level 0 players unless the user asks for them. */
  hidden?: boolean;
  items: FallbackItem[];
}

export interface FallbackItem {
  label: string;
  action: Action;
}
