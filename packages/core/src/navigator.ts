/**
 * Menu navigation as a pure state machine: step(manifest, state, event) → { state, effects }.
 *
 * Players feed it input and playback events and carry out the effects. The conformance suite
 * describes expected behaviour in exactly these terms, so every implementation (TS, C#, native)
 * can be checked against the same cases.
 */
import type { Action, Button, Direction, Manifest, Menu, PlayItem } from "./types.js";

export type Key = Direction | "enter" | "back";

export type NavEvent =
  /** Begin: run firstPlay, or open the root menu. */
  | { type: "start" }
  /** Remote/keyboard input. While playing, only "back" is handled (it stops playback). */
  | { type: "press"; key: Key }
  /** Pointer or touch moved onto a button. */
  | { type: "select"; button: string }
  /** Pointer or touch clicked a button. */
  | { type: "activate"; button: string }
  /** The current playback item finished on its own. */
  | { type: "ended" }
  /** The viewer stopped playback early; position (seconds) is remembered for "resume". */
  | { type: "stopped"; position: number }
  /** v0.2: the menu intro reached `buttonsAt`; buttons appear and respond. */
  | { type: "buttonsReady" }
  /** v0.2: a transition clip finished playing. */
  | { type: "transitionEnded" };

export type Effect =
  /** Start playing a media item from `start` seconds. */
  | { type: "play"; media: string; start: number }
  /** Briefly show the button's activated state. */
  | { type: "activated"; menu: string; button: string }
  /** "back" was pressed at the top of the menu history; the host may close the player. */
  | { type: "exit" }
  /** v0.2: play a transition clip; report "transitionEnded" when it finishes. */
  | { type: "transition"; src: string }
  /** v0.2: the viewer skipped the menu intro; jump the background to `buttonsAt`. */
  | { type: "skipIntro"; menu: string };

export interface MenuPosition {
  menu: string;
  focus: string;
}

export interface NavState {
  mode: "idle" | "menu" | "playing" | "fallback" | "transition";
  menu?: string;
  focus?: string;
  /**
   * v0.2: true when the current menu was entered fresh (start or goto), so its background plays its
   * intro from 0. False when returning to it (back, end of playback): the intro is not replayed.
   */
  intro: boolean;
  /** v0.2: false while a menu intro runs before `buttonsAt`; buttons are hidden and inactive. */
  buttonsReady: boolean;
  /** v0.2: the menu a running transition leads to. */
  transitionTo?: { menu: string; button?: string };
  /** Menus to return to with "back". */
  history: MenuPosition[];
  /** Playback in progress: the item playing now and those still queued. */
  playing?: { item: PlayItem; queue: PlayItem[]; after?: Action; returnTo?: MenuPosition };
  /** Where the viewer stopped last, for "resume". */
  lastStop?: { media: string; position: number };
}

export interface StepResult {
  state: NavState;
  effects: Effect[];
}

export const initialState = (): NavState => ({ mode: "idle", history: [], intro: false, buttonsReady: true });

export function step(manifest: Manifest, state: NavState, event: NavEvent): StepResult {
  const effects: Effect[] = [];
  const s: NavState = { ...state, history: [...state.history] };

  switch (event.type) {
    case "start":
      s.history = [];
      s.playing = undefined;
      if (manifest.firstPlay) run(manifest, s, manifest.firstPlay, effects, { pushHistory: false });
      else openRoot(manifest, s);
      break;

    case "press":
    case "select":
    case "activate":
      // v0.2: any key or click skips a transition or a menu intro; the viewer is always in control.
      // Pointer hover ("select") is ignored meanwhile, so moving the mouse doesn't skip anything.
      if ((s.mode === "transition" || (s.mode === "menu" && !s.buttonsReady)) && event.type === "select") break;
      if (s.mode === "transition") {
        endTransition(manifest, s);
        break;
      }
      if (s.mode === "menu" && !s.buttonsReady) {
        s.buttonsReady = true;
        effects.push({ type: "skipIntro", menu: s.menu! });
        break;
      }
      handleInput(manifest, s, event, effects);
      break;

    case "buttonsReady":
      if (s.mode === "menu") s.buttonsReady = true;
      break;

    case "transitionEnded":
      if (s.mode === "transition") endTransition(manifest, s);
      break;

    case "ended":
      if (s.mode !== "playing" || !s.playing) break;
      if (s.playing.queue.length > 0) {
        const [next, ...rest] = s.playing.queue as [PlayItem, ...PlayItem[]];
        s.playing = { ...s.playing, item: next, queue: rest };
        effects.push(playEffect(manifest, next));
      } else {
        finishPlayback(manifest, s, effects, { completed: true });
      }
      break;

    case "stopped":
      if (s.mode !== "playing") break;
      finishPlayback(manifest, s, effects, { stoppedAt: event.position });
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
      if (s.mode === "playing") {
        // Players report the position with "stopped"; a bare "back" stops at an unknown position.
        if (event.key === "back") finishPlayback(manifest, s, effects, { stoppedAt: undefined });
        break;
      }
      if (s.mode !== "menu") break;
      if (event.key === "enter") activate(manifest, s, s.focus!, effects);
      else if (event.key === "back") goBack(manifest, s, effects);
      else move(manifest, s, event.key, effects);
      break;

    case "select": {
      if (s.mode !== "menu" || event.button === s.focus) break;
      const button = findButton(menuOf(manifest, s.menu!), event.button);
      if (button) focusButton(manifest, s, button, effects);
      break;
    }

    case "activate":
      if (s.mode !== "menu") break;
      if (findButton(menuOf(manifest, s.menu!), event.button)) {
        s.focus = event.button;
        activate(manifest, s, event.button, effects);
      }
      break;

  }
}

// --- helpers -------------------------------------------------------------------------------

function menuOf(manifest: Manifest, id: string): Menu {
  const menu = manifest.menus?.[id];
  if (!menu) throw new Error(`No menu "${id}"`);
  return menu;
}

function findButton(menu: Menu, id: string): Button | undefined {
  return menu.buttons.find((b) => b.id === id);
}

function openRoot(manifest: Manifest, s: NavState) {
  if (!manifest.rootMenu || !manifest.menus) {
    s.mode = "fallback";
    s.menu = s.focus = undefined;
    return;
  }
  enterMenu(manifest, s, manifest.rootMenu, undefined, "intro");
}

function enterMenu(manifest: Manifest, s: NavState, menuId: string, buttonId: string | undefined, how: "intro" | "return") {
  const menu = menuOf(manifest, menuId);
  s.mode = "menu";
  s.menu = menuId;
  s.focus = buttonId ?? menu.defaultButton ?? menu.buttons[0]!.id;
  s.intro = how === "intro";
  s.buttonsReady = !(s.intro && (menu.buttonsAt ?? 0) > 0);
  s.transitionTo = undefined;
}

function endTransition(manifest: Manifest, s: NavState) {
  const to = s.transitionTo!;
  enterMenu(manifest, s, to.menu, to.button, "intro");
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

function goBack(manifest: Manifest, s: NavState, effects: Effect[]) {
  const previous = s.history.pop();
  if (!previous) {
    effects.push({ type: "exit" });
    return;
  }
  enterMenu(manifest, s, previous.menu, previous.focus, "return");
}

function currentPosition(s: NavState): MenuPosition | undefined {
  return s.mode === "menu" && s.menu && s.focus ? { menu: s.menu, focus: s.focus } : undefined;
}

function run(manifest: Manifest, s: NavState, action: Action, effects: Effect[], opts: { pushHistory: boolean }) {
  switch (action.type) {
    case "goto": {
      const here = currentPosition(s);
      if (opts.pushHistory && here) s.history.push(here);
      if (action.transition) {
        s.mode = "transition";
        s.transitionTo = { menu: action.menu, button: action.button };
        effects.push({ type: "transition", src: action.transition });
      } else {
        enterMenu(manifest, s, action.menu, action.button, "intro");
      }
      break;
    }
    case "back":
      goBack(manifest, s, effects);
      break;
    case "play":
      startPlayback(manifest, s, [action], action.after, effects);
      break;
    case "playAll":
      startPlayback(manifest, s, action.items, action.after, effects);
      break;
    case "resume":
      if (s.lastStop) startPlayback(manifest, s, [{ media: s.lastStop.media, start: s.lastStop.position }], undefined, effects);
      break;
  }
}

function startPlayback(manifest: Manifest, s: NavState, items: PlayItem[], after: Action | undefined, effects: Effect[]) {
  const [first, ...queue] = items as [PlayItem, ...PlayItem[]];
  s.playing = { item: first, queue, after, returnTo: currentPosition(s) };
  s.mode = "playing";
  effects.push(playEffect(manifest, first));
}

function finishPlayback(
  manifest: Manifest,
  s: NavState,
  effects: Effect[],
  how: { completed: true } | { stoppedAt: number | undefined },
) {
  const playing = s.playing!;
  s.playing = undefined;
  if ("completed" in how) {
    if (s.lastStop?.media === playing.item.media) s.lastStop = undefined;
  } else if (how.stoppedAt !== undefined) {
    s.lastStop = { media: playing.item.media, position: how.stoppedAt };
  }

  // Go back to where playback was started from, then run the after-action from there.
  if (playing.returnTo) {
    enterMenu(manifest, s, playing.returnTo.menu, playing.returnTo.focus, "return");
  } else {
    openRoot(manifest, s);
  }
  if ("completed" in how && playing.after) run(manifest, s, playing.after, effects, { pushHistory: true });
}

function playEffect(manifest: Manifest, item: PlayItem): Effect {
  return { type: "play", media: item.media, start: startTime(manifest, item) };
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
export function nearestButton(menu: Menu, from: Button, dir: Direction): Button | undefined {
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
