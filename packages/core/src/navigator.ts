/**
 * Menu navigation as a pure state machine: step(manifest, state, event) → { state, effects }.
 *
 * Players feed it input and playback events and carry out the effects. The conformance suite
 * describes expected behaviour in exactly these terms, so every implementation (TS, C#, native)
 * can be checked against the same cases.
 */
import type { Action, Button, Cue, Direction, Hold, Manifest, Menu, MenuKey, Overlay, PlayItem, Scalar } from "./types.js";
import { applyOp, evalTest, MENU_KEYS, readVar, valueOf } from "./logic.js";

/** A remote key: the arrows, Enter and Back, and (Level 2) the menu keys (spec §6.6). */
export type Key = Direction | "enter" | "back" | MenuKey;

export type NavEvent =
  /**
   * Begin: run firstPlay, or open the root menu. Players pass what they know of the viewer's setup as
   * "@" player state (languages, region, …) and a random seed.
   */
  | { type: "start"; player?: Record<string, Scalar>; seed?: number }
  /** Level 2: player state changed outside the menus (e.g. the viewer picked another soundtrack). */
  | { type: "player"; values: Record<string, Scalar> }
  /**
   * Remote/keyboard input. While playing, only "back" and the menu keys are handled. With a menu key
   * during playback, players pass the position (seconds) it was pressed at, for the resume point.
   */
  | { type: "press"; key: Key; position?: number }
  /** Pointer or touch moved onto a button. */
  | { type: "select"; button: string }
  /** Pointer or touch clicked a button. */
  | { type: "activate"; button: string }
  /** The current playback item finished on its own. */
  | { type: "ended" }
  /** The viewer stopped playback early; position (seconds) is remembered for "resume". */
  | { type: "stopped"; position: number }
  /** The menu intro reached `buttonsAt`; buttons appear and respond. */
  | { type: "buttonsReady" }
  /** A transition clip finished playing. */
  | { type: "transitionEnded" }
  /** Level 2: the countdown a "startTimer" effect asked for ran out on [menu]. */
  | { type: "timeout"; menu: string }
  /**
   * Level 2: playback passed a cue the play effect listed: a point cue's `at`, or an overlay's `from`
   * (its buttons show). Not for the time playback starts from, nor when seeking past it.
   */
  | { type: "cue"; id: string }
  /** Level 2: playback passed an overlay's `to` (or left its window by seeking): its buttons go. */
  | { type: "cueEnd"; id: string };

/** Level 2: a cue the host watches for during playback, from the play effect. */
export type CueWatch = { id: string; at: number } | { id: string; from: number; to: number };

export type Effect =
  /** Start playing a media item from `start` seconds. */
  | {
      type: "play";
      media: string;
      start: number;
      /** Level 2 */
      audio?: number;
      subtitle?: number;
      /** Level 2: which angle, for media with angles (§4.1): 1 = ref, 2 = angles[0] or video track 2, … */
      angle?: number;
      /** Level 2: stop here and report "ended" (§6.3). */
      end?: number;
      /** Level 2: the cues to report with "cue"/"cueEnd" while this plays (§6.3). */
      cues?: CueWatch[];
      /** Level 2: what the source forbids while this plays (the media's and the item's), for players that honour it (§6.6). */
      prohibits?: string[];
      /** Level 2: frames to hold (the media's and the item's, in time order), which the player carries out (§6.3). */
      holds?: Hold[];
    }
  /** Briefly show the button's activated state (of a menu, or of the overlay showing during playback). */
  | { type: "activated"; menu: string; button: string }
  | { type: "activated"; cue: string; button: string }
  /** "back" was pressed at the top of the menu history; the host may close the player. */
  | { type: "exit" }
  /** Play a transition clip; report "transitionEnded" when it finishes. */
  | { type: "transition"; src: string }
  /** The viewer skipped the menu intro; jump the background to `buttonsAt`. */
  | { type: "skipIntro"; menu: string }
  /**
   * Level 2: (re)start the countdown for [menu]'s timeout: report {type: "timeout", menu} after
   * [after] seconds, unless another startTimer replaces it or the viewer leaves that menu first.
   */
  | { type: "startTimer"; menu: string; after: number }
  /**
   * Level 2: move the playback in progress to [position] seconds on its timeline (a seek action, §6.3).
   * Cues behave as when the viewer seeks: point cues on the way are skipped, overlays follow the position.
   */
  | { type: "seek"; position: number }
  /**
   * Level 2: the menus changed a track of the playback in progress (a set on @audio, @subtitle or
   * @angle during playback, a disc's SetSTN in a cell command): apply it now, from where playback is
   * (`at`, the cue's time, when the player doesn't know better).
   */
  | { type: "tracks"; audio?: number; subtitle?: number; angle?: number; at: number }
  /**
   * Level 2: one event ran STEP_BUDGET actions (a named action running itself without end); the
   * rest were dropped. For diagnostics: players carry on with the state as it is.
   */
  | { type: "budgetExceeded" };

export interface MenuPosition {
  menu: string;
  focus: string;
}

export interface NavState {
  mode: "idle" | "menu" | "playing" | "fallback" | "transition";
  menu?: string;
  focus?: string;
  /**
   * True when the current menu was entered fresh (start or goto), so its background plays its
   * intro from 0. False when returning to it (back, end of playback): the intro is not replayed.
   */
  intro: boolean;
  /** False while a menu intro runs before `buttonsAt`; buttons are hidden and inactive. */
  buttonsReady: boolean;
  /** The menu a running transition leads to. */
  transitionTo?: { menu: string; button?: string };
  /** Menus to return to with "back". */
  history: MenuPosition[];
  /**
   * Playback in progress: the item playing now and those still queued. `resumeTo`: playback a cue
   * interrupted, which continues from `at` when this ends or is stopped (Level 2).
   */
  playing?: Playing;
  /** Level 2: the overlay showing during playback (its cue id) and its focused button. */
  overlay?: { cue: string; focus: string };
  /**
   * The resume point, for "resume": where the viewer stopped last (or a saveResume put it). Hosts may
   * seed it from their own records with just media and position. Set by the navigator, it also holds
   * the rest of that playback (the item, what was queued after it, its after-action), so resuming
   * continues a play-all as a disc does.
   */
  lastStop?: ResumePoint;
  /** Level 2: variable values for this session (from manifest.vars, changed by "set"). */
  vars: Record<string, Scalar>;
  /** Level 2: player state ("@audio", "@audioLanguage", …) as reported by the player or changed by set. */
  player: Record<string, Scalar>;
  /** Level 2: the random generator's state (see logic.ts nextRandom). */
  seed: number;
  /** Counts menu entries and playback starts, so "do" knows when a step navigated. */
  seq: number;
}

export interface ResumePoint {
  media: string;
  position: number;
  item?: PlayItem;
  queue?: PlayItem[];
  after?: Action;
}

export interface Playing {
  item: PlayItem;
  queue: PlayItem[];
  after?: Action;
  returnTo?: MenuPosition;
  resumeTo?: { playing: Playing; at: number };
}

export interface StepResult {
  state: NavState;
  effects: Effect[];
}

export const initialState = (): NavState => ({ mode: "idle", history: [], intro: false, buttonsReady: true, vars: {}, player: {}, seed: 1, seq: 0 });

/** Level 2: the most actions one event runs (spec §6.4), so a named action that runs itself still ends. */
export const STEP_BUDGET = 10_000;

/** Actions the current event may still run; step() is synchronous, so one counter serves. */
let stepsLeft = STEP_BUDGET;

/** The current event saved or cleared the resume point itself, so leaving playback keeps that (Level 2). */
let resumeSet = false;

export function step(manifest: Manifest, state: NavState, event: NavEvent): StepResult {
  const effects: Effect[] = [];
  stepsLeft = STEP_BUDGET;
  resumeSet = false;
  const s: NavState = { ...state, history: [...state.history] };

  switch (event.type) {
    case "start":
      s.history = [];
      s.playing = undefined;
      s.vars = { ...(manifest.vars ?? {}) };
      s.player = { ...(s.player ?? {}), ...(event.player ?? {}) };
      if (event.seed !== undefined) s.seed = event.seed;
      if (manifest.firstPlay) run(manifest, s, manifest.firstPlay, effects, { pushHistory: false });
      else openRoot(manifest, s, effects);
      break;

    case "press":
    case "select":
    case "activate": {
      // Any key or click skips a transition or a menu intro; the viewer is always in control.
      // Pointer hover ("select") is ignored meanwhile, so moving the mouse doesn't skip anything.
      if ((s.mode === "transition" || (s.mode === "menu" && !s.buttonsReady)) && event.type === "select") break;
      if (s.mode === "transition") {
        endTransition(manifest, s, effects);
        break;
      }
      const inMenu = s.mode === "menu" ? s.menu : undefined;
      if (s.mode === "menu" && !s.buttonsReady) {
        s.buttonsReady = true;
        effects.push({ type: "skipIntro", menu: s.menu! });
      } else {
        handleInput(manifest, s, event, effects);
      }
      // Input restarts the countdown of a menu still showing (unless it says restartOnInput: false),
      // but not when the viewer is leaving (exit).
      if (inMenu && s.mode === "menu" && s.menu === inMenu && !effects.some((e) => e.type === "startTimer" || e.type === "exit")) {
        const timeout = menuOf(manifest, inMenu).timeout;
        if (timeout && timeout.restartOnInput !== false) effects.push({ type: "startTimer", menu: inMenu, after: timeout.after });
      }
      break;
    }

    case "timeout": {
      // Only for the menu still showing: a countdown from a menu the viewer has left is stale.
      if (s.mode !== "menu" || s.menu !== event.menu) break;
      const timeout = menuOf(manifest, s.menu).timeout;
      // Nobody chose this: it runs as a step up, like a menu's back. A goto adds no history, and one
      // to a menu already in history unwinds it (a submenu timing out to main leaves nothing behind).
      if (timeout) run(manifest, s, timeout.action, effects, { pushHistory: false, up: true });
      break;
    }

    case "buttonsReady":
      if (s.mode === "menu") s.buttonsReady = true;
      break;

    case "transitionEnded":
      if (s.mode === "transition") endTransition(manifest, s, effects);
      break;

    case "ended":
      if (s.mode !== "playing" || !s.playing) break;
      if (s.playing.queue.length > 0) {
        const [next, ...rest] = s.playing.queue as [PlayItem, ...PlayItem[]];
        s.playing = { ...s.playing, item: next, queue: rest };
        s.overlay = undefined;
        effects.push(startItem(manifest, s, next));
      } else {
        finishPlayback(manifest, s, effects, { completed: true });
      }
      break;

    case "stopped":
      if (s.mode !== "playing") break;
      finishPlayback(manifest, s, effects, { stoppedAt: event.position });
      break;

    case "player":
      s.player = { ...s.player, ...event.values };
      break;

    case "cue": {
      if (s.mode !== "playing" || !s.playing) break;
      const cue = cueById(manifest, s.playing.item, event.id);
      if (!cue) break;
      if ("at" in cue) run(manifest, s, cue.action, effects, { pushHistory: false, cueAt: cue.at });
      else {
        // The overlay's buttons show; a later one replaces one still showing.
        const focus = buttonRef(s, cue, cue.defaultButton) ?? cue.buttons[0]!.id;
        s.overlay = { cue: event.id, focus };
      }
      break;
    }

    case "cueEnd":
      if (s.overlay?.cue === event.id) s.overlay = undefined;
      break;
  }
  return { state: s, effects };
}

function handleInput(
  manifest: Manifest,
  s: NavState,
  event: Extract<NavEvent, { type: "press" | "select" | "activate" }>,
  effects: Effect[],
) {
  switch (event.type) {
    case "press":
      if (isMenuKey(event.key)) {
        menuKey(manifest, s, event.key, event.position, effects);
        break;
      }
      if (s.mode === "playing") {
        // Players report the position with "stopped"; a bare "back" stops at an unknown position.
        if (event.key === "back") finishPlayback(manifest, s, effects, { stoppedAt: undefined });
        // With an overlay showing, the arrows and Enter are its; otherwise they're the video player's.
        else if (s.overlay) {
          if (event.key === "enter") activateOverlay(manifest, s, s.overlay.focus, effects);
          else moveOverlay(manifest, s, event.key, effects);
        }
        break;
      }
      if (s.mode !== "menu") break;
      if (event.key === "enter") activate(manifest, s, s.focus!, effects);
      else if (event.key === "back") remoteBack(manifest, s, effects);
      else move(manifest, s, event.key, effects);
      break;

    case "select": {
      if (s.mode === "playing" && s.overlay) {
        const overlay = overlayOf(manifest, s);
        if (overlay && event.button !== s.overlay.focus && findButton(overlay, event.button)) s.overlay = { ...s.overlay, focus: event.button };
        break;
      }
      if (s.mode !== "menu" || event.button === s.focus) break;
      const button = findButton(menuOf(manifest, s.menu!), event.button);
      if (button) focusButton(manifest, s, button, effects);
      break;
    }

    case "activate":
      if (s.mode === "playing" && s.overlay) {
        const overlay = overlayOf(manifest, s);
        if (overlay && findButton(overlay, event.button)) activateOverlay(manifest, s, event.button, effects);
        break;
      }
      if (s.mode !== "menu") break;
      if (findButton(menuOf(manifest, s.menu!), event.button)) {
        s.focus = event.button;
        activate(manifest, s, event.button, effects);
      }
      break;

  }
}

// --- helpers -------------------------------------------------------------------------------

function isMenuKey(key: Key): key is MenuKey {
  return (MENU_KEYS as readonly string[]).includes(key);
}

/**
 * Level 2: a menu key (spec §6.6). During playback it runs like a cue's action at the position it was
 * pressed: a menu it opens leaves playback, remembered for resume; playback it starts returns there.
 * The item's keys win over the media's, the media's over the manifest's. On a menu, the menu's keys
 * win over the manifest's, run as a step up. Without an action, Menu and Title open the root menu.
 */
function menuKey(manifest: Manifest, s: NavState, key: MenuKey, position: number | undefined, effects: Effect[]) {
  const fallback: Action | undefined = (key === "menu" || key === "title") && manifest.rootMenu ? { type: "goto", menu: manifest.rootMenu } : undefined;
  if (s.mode === "playing" && s.playing) {
    const item = s.playing.item;
    const action = item.keys?.[key] ?? manifest.media[item.media]?.keys?.[key] ?? manifest.keys?.[key] ?? fallback;
    if (action) run(manifest, s, action, effects, { pushHistory: false, cueAt: position ?? startTime(manifest, item) });
  } else if (s.mode === "menu" && s.menu) {
    const action = menuOf(manifest, s.menu).keys?.[key] ?? manifest.keys?.[key] ?? fallback;
    if (action) run(manifest, s, action, effects, { pushHistory: false, up: true });
  }
}

function menuOf(manifest: Manifest, id: string): Menu {
  const menu = manifest.menus?.[id];
  if (!menu) throw new Error(`No menu "${id}"`);
  return menu;
}

function findButton(menu: { buttons: Button[] }, id: string): Button | undefined {
  return menu.buttons.find((b) => b.id === id);
}

/** Level 2: the cues that apply while [item] plays, with their ids: the media's, then the item's. */
function cuesOf(manifest: Manifest, item: PlayItem): { id: string; cue: Cue }[] {
  return [
    ...(manifest.media[item.media]?.cues ?? []).map((cue, i) => ({ id: `media/${i}`, cue })),
    ...(item.cues ?? []).map((cue, i) => ({ id: `item/${i}`, cue })),
  ];
}

function cueById(manifest: Manifest, item: PlayItem, id: string): Cue | undefined {
  return cuesOf(manifest, item).find((c) => c.id === id)?.cue;
}

/** Level 2: the overlay whose buttons show now (state.overlay), for renderers to draw. */
export function overlayOf(manifest: Manifest, s: NavState): Overlay | undefined {
  const cue = s.overlay && s.playing ? cueById(manifest, s.playing.item, s.overlay.cue) : undefined;
  return cue && "from" in cue ? cue : undefined;
}

function moveOverlay(manifest: Manifest, s: NavState, dir: Direction, effects: Effect[]) {
  const overlay = overlayOf(manifest, s);
  if (!overlay || !s.overlay) return;
  const current = findButton(overlay, s.overlay.focus)!;
  let targetId = current.nav?.[dir];
  if (!targetId && overlay.autoNav) targetId = nearestButton(overlay, current, dir)?.id;
  if (!targetId || targetId === current.id) return;
  s.overlay = { ...s.overlay, focus: targetId };
  const target = findButton(overlay, targetId)!;
  if (target.autoAction) activateOverlay(manifest, s, targetId, effects);
}

/** Press an overlay button: its action runs at the overlay's end time (where interrupted playback resumes). */
function activateOverlay(manifest: Manifest, s: NavState, buttonId: string, effects: Effect[]) {
  const overlay = overlayOf(manifest, s);
  const button = overlay && findButton(overlay, buttonId);
  if (!overlay || !button || !s.overlay) return;
  s.overlay = { ...s.overlay, focus: button.id };
  effects.push({ type: "activated", cue: s.overlay.cue, button: button.id });
  run(manifest, s, button.action, effects, { pushHistory: false, cueAt: overlay.to });
}

function openRoot(manifest: Manifest, s: NavState, effects: Effect[]) {
  if (!manifest.rootMenu || !manifest.menus) {
    s.mode = "fallback";
    s.menu = s.focus = undefined;
    return;
  }
  enterMenu(manifest, s, manifest.rootMenu, undefined, "intro", effects);
}

/** A button reference (an id, or a variable holding one) resolved to a button of the menu, if it is one. */
function buttonRef(s: NavState, menu: { buttons: Button[] }, ref: string | { var: string } | undefined): string | undefined {
  if (ref === undefined) return undefined;
  const id = typeof ref === "object" ? String(readVar(s, ref.var)) : ref;
  return findButton(menu, id) ? id : undefined;
}

function enterMenu(manifest: Manifest, s: NavState, menuId: string, buttonId: string | { var: string } | undefined, how: "intro" | "return", effects: Effect[]) {
  const menu = menuOf(manifest, menuId);
  s.mode = "menu";
  s.menu = menuId;
  s.seq += 1;
  s.focus = buttonRef(s, menu, buttonId) ?? buttonRef(s, menu, menu.defaultButton) ?? menu.buttons[0]!.id;
  s.intro = how === "intro";
  s.buttonsReady = !(s.intro && (menu.buttonsAt ?? 0) > 0);
  s.transitionTo = undefined;
  // Every time the menu appears (fresh or returned to), its countdown starts over.
  if (menu.timeout) effects.push({ type: "startTimer", menu: menuId, after: menu.timeout.after });
}

function endTransition(manifest: Manifest, s: NavState, effects: Effect[]) {
  const to = s.transitionTo!;
  enterMenu(manifest, s, to.menu, to.button, "intro", effects);
}

function focusButton(manifest: Manifest, s: NavState, button: Button, effects: Effect[]) {
  s.focus = button.id;
  if (button.autoAction) activate(manifest, s, button.id, effects);
}

function move(manifest: Manifest, s: NavState, dir: Direction, effects: Effect[]) {
  const menu = menuOf(manifest, s.menu!);
  const current = findButton(menu, s.focus!)!;
  let targetId = current.nav?.[dir];
  if (!targetId && menu.autoNav) targetId = nearestButton(menu, current, dir)?.id;
  if (!targetId || targetId === current.id) return;
  focusButton(manifest, s, findButton(menu, targetId)!, effects);
}

function activate(manifest: Manifest, s: NavState, buttonId: string, effects: Effect[]) {
  const button = findButton(menuOf(manifest, s.menu!), buttonId)!;
  effects.push({ type: "activated", menu: s.menu!, button: button.id });
  run(manifest, s, button.action, effects, { pushHistory: true });
}

/**
 * The remote's Back key on a menu. A menu's `back` action says where Back goes (like a DVD's
 * "Go Up"), run as a step up: no history is added, and a target already in history unwinds it, so
 * paging through ten bios never means ten presses. Without `back`, Back returns through history,
 * except on the root menu, where it leaves the title however the viewer got there (a "return to the
 * main menu" button adds history, but the main menu is still the top). Back must never trap the
 * viewer: if `back` leaves them on the same menu, they step back (or leave, on the root menu).
 */
function remoteBack(manifest: Manifest, s: NavState, effects: Effect[]) {
  const menu = menuOf(manifest, s.menu!);
  if (!menu.back) {
    backWithoutTarget(manifest, s, effects);
    return;
  }
  const from = s.menu;
  run(manifest, s, menu.back, effects, { pushHistory: false, up: true });
  if (s.mode === "menu" && s.menu === from) backWithoutTarget(manifest, s, effects);
}

function backWithoutTarget(manifest: Manifest, s: NavState, effects: Effect[]) {
  if (s.menu === manifest.rootMenu) effects.push({ type: "exit" });
  else goBack(manifest, s, effects);
}

function goBack(manifest: Manifest, s: NavState, effects: Effect[]) {
  const previous = s.history.pop();
  if (!previous) {
    effects.push({ type: "exit" });
    return;
  }
  enterMenu(manifest, s, previous.menu, previous.focus, "return", effects);
}

function currentPosition(s: NavState): MenuPosition | undefined {
  return s.mode === "menu" && s.menu && s.focus ? { menu: s.menu, focus: s.focus } : undefined;
}

/**
 * `up`: running a menu's `back` (see remoteBack): gotos unwind history instead of adding to it.
 * `cueAt`: running a cue's action during playback (Level 2); the time interrupted playback resumes from.
 */
function run(manifest: Manifest, s: NavState, first: Action, effects: Effect[], opts: { pushHistory: boolean; up?: boolean; cueAt?: number }) {
  // An action in tail position (set's then, if's branches, do's last step, resume's otherwise, a
  // pressed button's action, a named action) continues this loop instead of nesting a call, so a
  // disc's loop, written as a named action that runs itself, goes round as often as the budget allows.
  let next: Action | undefined = first;
  while (next) {
    if (stepsLeft <= 0) {
      if (stepsLeft === 0) effects.push({ type: "budgetExceeded" });
      stepsLeft = -1;
      return;
    }
    stepsLeft--;
    const action: Action = next;
    next = undefined;
    const duringPlayback = opts.cueAt !== undefined && s.mode === "playing" && s.playing !== undefined;
    switch (action.type) {
      case "goto": {
        // A cue opening a menu leaves playback, remembered for resume where the cue is.
        if (duringPlayback) leavePlayback(manifest, s, opts.cueAt!);
        const here = currentPosition(s);
        if (opts.up) {
          // Going up to a menu already in history: drop it and everything after it.
          const i = s.history.map((h) => h.menu).lastIndexOf(action.menu);
          if (i >= 0) s.history.length = i;
        } else if (opts.pushHistory && here && action.history !== "replace") s.history.push(here);
        if (action.transition) {
          s.mode = "transition";
          // A variable button is read now, when the viewer chose it.
          const button = buttonRef(s, menuOf(manifest, action.menu), action.button);
          s.transitionTo = { menu: action.menu, ...(button !== undefined ? { button } : {}) };
          effects.push({ type: "transition", src: action.transition });
        } else {
          enterMenu(manifest, s, action.menu, action.button, "intro", effects);
        }
        break;
      }
      case "back":
        // From a cue: like the remote's Back during playback.
        if (duringPlayback) finishPlayback(manifest, s, effects, { stoppedAt: opts.cueAt });
        else goBack(manifest, s, effects);
        break;
      case "play":
        startPlayback(manifest, s, [action], action.after, effects, opts.cueAt);
        break;
      case "playAll":
        startPlayback(manifest, s, action.items, action.after, effects, opts.cueAt);
        break;
      case "resume":
        if (s.lastStop) {
          // The stopped playback continues: the item from where it stopped (with the tracks as they are
          // now, which the viewer may have changed while watching), then what was queued after it, then
          // its after-action. A disc's resume point is used up by resuming (Level 2).
          const point = s.lastStop;
          const { chapter: _c, start: _s, audio: _a, subtitle: _t, angle: _g, ...item } = point.item ?? { media: point.media };
          if (manifest.resumePoint === "untilUsed") s.lastStop = undefined;
          startPlayback(manifest, s, [{ ...item, start: point.position }, ...(point.queue ?? [])], point.after, effects, opts.cueAt);
        }
        // Nothing to resume: the fallback runs as if it were the action itself (a DVD's "if stopped,
        // LinkRSM, else go to the picker").
        else next = action.otherwise;
        break;
      case "saveResume":
        if (action.media !== undefined) {
          s.lastStop = { media: action.media, position: action.chapter !== undefined ? startTime(manifest, { media: action.media, chapter: action.chapter }) : (action.at ?? 0) };
        } else if (s.mode === "playing" && s.playing) {
          // "Here": the playback in progress (a disc's CallSS names the cell to resume at).
          const media = s.playing.item.media;
          const at = action.chapter !== undefined ? startTime(manifest, { media, chapter: action.chapter }) : (action.at ?? opts.cueAt ?? 0);
          s.lastStop = resumePointOf(s.playing, at);
        }
        resumeSet = true;
        break;
      case "clearResume":
        s.lastStop = undefined;
        resumeSet = true;
        break;
      case "set": {
        const value = applyOp(s, readVar(s, action.var), action.op ?? "set", valueOf(s, action.value));
        if (value !== undefined) {
          if (action.var === "@focus") {
            // Highlight a button of the current menu (a DVD's SetHL_BTN).
            if (s.menu && findButton(menuOf(manifest, s.menu), String(value))) s.focus = String(value);
          } else if (action.var.startsWith("@")) {
            s.player = { ...s.player, [action.var]: value };
            if (duringPlayback) liveTrack(manifest, s, action.var, value, opts.cueAt!, effects);
          } else s.vars = { ...s.vars, [action.var]: value };
        }
        next = action.then;
        break;
      }
      case "seek":
        // Only during playback: the title continues elsewhere, as a link ends a disc's program.
        if (duringPlayback) {
          const item = s.playing!.item;
          const position = action.chapter !== undefined ? startTime(manifest, { media: item.media, chapter: action.chapter }) : (action.at ?? 0);
          s.overlay = undefined;
          s.seq += 1;
          s.player = { ...s.player, "@lastChapter": chapterAt(manifest, item.media, position) };
          effects.push({ type: "seek", position });
        }
        break;
      case "if":
        next = evalTest(s, action.test) ? action.then : action.else;
        break;
      case "do": {
        // In order, until a step navigates: opens a menu, starts playback or a transition, or leaves.
        const last = action.steps.length - 1;
        for (let i = 0; i < last; i++) {
          const before = { seq: s.seq, mode: s.mode, effects: effects.length };
          run(manifest, s, action.steps[i]!, effects, opts);
          const navigated =
            s.seq !== before.seq || s.mode !== before.mode || effects.slice(before.effects).some((e) => e.type === "exit" || e.type === "transition");
          if (navigated || stepsLeft < 0) return;
        }
        next = action.steps[last];
        break;
      }
      case "activate": {
        // Press a button of the current menu, as if the viewer had: focus, activated state, action.
        const button = s.menu ? findButton(menuOf(manifest, s.menu), action.button) : undefined;
        if (!button) break;
        s.focus = button.id;
        effects.push({ type: "activated", menu: s.menu!, button: button.id });
        next = button.action;
        break;
      }
      case "run":
        // As if the named action were written here: same history rules, same cue time.
        next = manifest.actions?.[action.action];
        break;
    }
  }
}

/** `cueAt`: started by a cue during playback, which it interrupts and returns to (Level 2). */
function startPlayback(manifest: Manifest, s: NavState, items: PlayItem[], after: Action | undefined, effects: Effect[], cueAt?: number) {
  const [first, ...queue] = items as [PlayItem, ...PlayItem[]];
  const interrupted = cueAt !== undefined && s.mode === "playing" ? s.playing : undefined;
  s.playing = interrupted
    ? { item: first, queue, returnTo: interrupted.returnTo, resumeTo: { playing: interrupted, at: cueAt! } }
    : { item: first, queue, after, returnTo: currentPosition(s) };
  s.mode = "playing";
  s.overlay = undefined;
  s.seq += 1;
  effects.push(startItem(manifest, s, first));
}

/**
 * A cue opened a menu: playback ends, remembered for resume at the cue's time, unless the cue saved
 * or cleared the resume point itself (a disc's CallSS names its resume cell; a JumpSS saves none).
 */
function leavePlayback(manifest: Manifest, s: NavState, at: number) {
  const media = s.playing!.item.media;
  if (!resumeSet) s.lastStop = resumePointOf(s.playing!, at);
  s.player = { ...s.player, "@lastChapter": chapterAt(manifest, media, at) };
  s.playing = undefined;
  s.overlay = undefined;
}

/**
 * Level 2: a set on @audio, @subtitle or @angle during playback applies now (a disc's SetSTN in a cell
 * command or a button over the film), not only to the next playback.
 */
function liveTrack(manifest: Manifest, s: NavState, name: string, value: Scalar, at: number, effects: Effect[]) {
  if (typeof value !== "number") return;
  if (name === "@audio" && value > 0) effects.push({ type: "tracks", audio: value, at });
  else if (name === "@subtitle") effects.push({ type: "tracks", subtitle: value, at });
  else if (name === "@angle") {
    const angles = angleCount(manifest, s.playing!.item.media);
    if (angles > 1) effects.push({ type: "tracks", angle: value >= 1 && value <= angles ? value : 1, at });
  }
}

/** The resume point for [playing] stopped (or left) at [at]: the item, what's queued after it, its after-action. */
function resumePointOf(playing: Playing, at: number): ResumePoint {
  return {
    media: playing.item.media,
    position: at,
    item: playing.item,
    ...(playing.queue.length ? { queue: playing.queue } : {}),
    ...(playing.after ? { after: playing.after } : {}),
  };
}

/** The play effect for an item, noting it as the last playback (@lastMedia, @lastChapter). `from`: start elsewhere than the item's own start. */
function startItem(manifest: Manifest, s: NavState, item: PlayItem, from?: number): Effect {
  const effect = playEffect(manifest, item, s, from);
  s.player = { ...s.player, "@lastMedia": item.media, "@lastChapter": chapterAt(manifest, item.media, effect.type === "play" ? effect.start : 0) };
  return effect;
}

/** The 1-based chapter a time falls in (0 when the media has no chapters). */
export function chapterAt(manifest: Manifest, media: string, seconds: number): number {
  const chapters = manifest.media[media]?.chapters ?? [];
  let n = 0;
  chapters.forEach((c, i) => {
    if (c.time <= seconds) n = i + 1;
  });
  return n;
}

function finishPlayback(
  manifest: Manifest,
  s: NavState,
  effects: Effect[],
  how: { completed: true } | { stoppedAt: number | undefined },
) {
  const playing = s.playing!;
  s.playing = undefined;
  s.overlay = undefined;
  // Playback a cue started: the interrupted one continues where the cue was (Level 2).
  if (playing.resumeTo) {
    if ("completed" in how && forgetsOnEnd(manifest, s, playing)) s.lastStop = undefined;
    s.playing = playing.resumeTo.playing;
    s.mode = "playing";
    s.seq += 1;
    effects.push(startItem(manifest, s, playing.resumeTo.playing.item, playing.resumeTo.at));
    return;
  }
  if ("completed" in how) {
    if (forgetsOnEnd(manifest, s, playing)) s.lastStop = undefined;
  } else if (how.stoppedAt !== undefined) {
    s.lastStop = resumePointOf(playing, how.stoppedAt);
    s.player = { ...s.player, "@lastChapter": chapterAt(manifest, playing.item.media, how.stoppedAt) };
  }

  // Go back to where playback was started from, then run the after-action from there.
  if (playing.returnTo) {
    enterMenu(manifest, s, playing.returnTo.menu, playing.returnTo.focus, "return", effects);
  } else {
    openRoot(manifest, s, effects);
  }
  if ("completed" in how && playing.after) run(manifest, s, playing.after, effects, { pushHistory: true });
}

/** Playback of the resume point's media completed: forget it, unless it lasts until used (a disc's). */
function forgetsOnEnd(manifest: Manifest, s: NavState, playing: Playing): boolean {
  return manifest.resumePoint !== "untilUsed" && s.lastStop?.media === playing.item.media;
}

function playEffect(manifest: Manifest, item: PlayItem, s: NavState, from?: number): Effect {
  const value = (v: PlayItem["audio"]) => {
    if (v === undefined) return undefined;
    const n = typeof v === "number" ? v : readVar(s, v.var);
    return typeof n === "number" ? n : undefined;
  };
  // Without tracks of its own, playback uses the current ones (after a set on @audio/@subtitle,
  // like a DVD's SetSTN, or as the player reported them).
  const audio = value(item.audio) ?? (typeof s.player["@audio"] === "number" && s.player["@audio"] > 0 ? s.player["@audio"] : undefined);
  const subtitle = value(item.subtitle) ?? (typeof s.player["@subtitle"] === "number" ? s.player["@subtitle"] : undefined);
  const holds = [...(manifest.media[item.media]?.holds ?? []), ...(item.holds ?? [])].sort((a, b) => a.at - b.at);
  const prohibits = [...new Set([...(manifest.media[item.media]?.prohibits ?? []), ...(item.prohibits ?? [])])];
  const cues: CueWatch[] = cuesOf(manifest, item).map(({ id, cue }) => ("at" in cue ? { id, at: cue.at } : { id, from: cue.from, to: cue.to }));
  // Media with angles plays the item's angle, otherwise @angle; one it doesn't have plays angle 1.
  const angles = angleCount(manifest, item.media);
  let angle: number | undefined;
  if (angles > 1) {
    const current = s.player["@angle"];
    const wanted = value(item.angle) ?? (typeof current === "number" ? current : 1);
    angle = wanted >= 1 && wanted <= angles ? wanted : 1;
  }
  return {
    type: "play",
    media: item.media,
    start: from ?? startTime(manifest, item),
    ...(audio !== undefined ? { audio } : {}),
    ...(subtitle !== undefined ? { subtitle } : {}),
    ...(angle !== undefined ? { angle } : {}),
    ...(item.end !== undefined ? { end: item.end } : {}),
    ...(cues.length ? { cues } : {}),
    ...(prohibits.length ? { prohibits } : {}),
    ...(holds.length ? { holds } : {}),
  };
}

/** How many angles a media item has (§4.1): 1 when it has none. */
export function angleCount(manifest: Manifest, media: string): number {
  const m = manifest.media[media];
  if (!m) return 1;
  // Made of segments: a play's angle applies to the segments whose media have angles (§4.2).
  if (m.segments) return Math.max(1, ...m.segments.map((s) => (manifest.media[s.media]?.segments ? 1 : angleCount(manifest, s.media))));
  if (m.angles) return 1 + m.angles.length;
  return m.angleTracks ?? 1;
}

/** Start offset in seconds for a play item (chapter wins over start). */
export function startTime(manifest: Manifest, item: PlayItem): number {
  if (item.chapter !== undefined) return manifest.media[item.media]?.chapters?.[item.chapter - 1]?.time ?? 0;
  return item.start ?? 0;
}

/**
 * Nearest button in a direction, for menus with autoNav. Candidates must lie in the direction of
 * travel (by centre); the score favours small distance along the axis and penalises sideways offset.
 */
export function nearestButton(menu: { buttons: Button[] }, from: Button, dir: Direction): Button | undefined {
  const centre = (b: Button) => [b.rect[0] + b.rect[2] / 2, b.rect[1] + b.rect[3] / 2] as const;
  const [fx, fy] = centre(from);
  let best: Button | undefined;
  let bestScore = Infinity;
  for (const b of menu.buttons) {
    if (b.id === from.id) continue;
    const [bx, by] = centre(b);
    const dx = bx - fx;
    const dy = by - fy;
    const along = dir === "left" ? -dx : dir === "right" ? dx : dir === "up" ? -dy : dy;
    const sideways = dir === "left" || dir === "right" ? Math.abs(dy) : Math.abs(dx);
    if (along <= 0) continue;
    const score = along + 2 * sideways;
    if (score < bestScore) {
      bestScore = score;
      best = b;
    }
  }
  return best;
}
