/**
 * MenuStream manifest v0.1 (draft) — TypeScript model.
 * The normative definition is spec/menustream-v0.1.md and spec/schema/manifest.schema.json.
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
  [extension: `x-${string}`]: unknown;
}

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
  ref: MediaRef;
  label?: string;
  kind?: MediaKind | string;
  /** Seconds. */
  duration?: number;
  chapters?: Chapter[];
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
  /** Motion background (v0.2): plays once from 0, then loops from `loopStart`. May carry its own audio. */
  video?: LoopingMedia;
  /** Menu audio (v0.2), looping the same way. */
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
  defaultButton?: string;
  /** When true, a missing nav direction moves to the nearest button in that direction. */
  autoNav?: boolean;
  buttons: Button[];
  /**
   * v0.2: seconds into the menu's intro before buttons appear and respond (DVD menus often animate in).
   * Measured on the background video when there is one. Any key skips straight to this point.
   */
  buttonsAt?: number;
  [extension: `x-${string}`]: unknown;
}

export interface PlayItem {
  media: string;
  /** 1-based chapter number within the media. */
  chapter?: number;
  /** Start offset in seconds (ignored when chapter is given). */
  start?: number;
}

export type Action =
  | ({ type: "play"; after?: Action } & PlayItem)
  | { type: "playAll"; items: PlayItem[]; after?: Action }
  | { type: "goto"; menu: string; button?: string; /** v0.2: clip played before the menu opens (skippable). */ transition?: string }
  | { type: "back" }
  | { type: "resume" };

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
