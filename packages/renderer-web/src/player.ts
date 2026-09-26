import {
  canvasAspect,
  initialState,
  resolveMedia,
  resolvePath,
  startTime,
  step,
  type Button,
  type ButtonState,
  type Effect,
  type FallbackItem,
  type Key,
  type Manifest,
  type MediaResolver,
  type Menu,
  type NavEvent,
  type NavState,
  type PlayItem,
} from "@menustream/core";
import { Html5Playback, KeyboardInput, type InputAdapter, type PlaybackAdapter } from "./adapters.js";
import { injectStyles } from "./styles.js";

export interface PlayerOptions {
  manifest: Manifest;
  /** Absolute URL of the manifest; relative paths in it resolve against this. */
  manifestUrl: string;
  /** Resolvers for {scheme, id} media refs, keyed by scheme. */
  resolvers?: Record<string, MediaResolver>;
  /** Factory for the playback adapter (default: HTML5 <video>). */
  playback?: (container: HTMLElement) => PlaybackAdapter;
  /** Input adapter (default: keyboard / TV-remote keys on window). */
  input?: InputAdapter;
  /** "fallback" shows the Level 0 list even when menus exist. */
  view?: "menu" | "fallback";
  /** Show hidden fallback groups (easter eggs). */
  showHidden?: boolean;
  /** "back" pressed at the top menu. */
  onExit?: () => void;
  onStateChange?: (state: NavState) => void;
  /** How long the activated state shows before the action runs (ms). */
  activatedFlashMs?: number;
}

type VisualState = "normal" | "selected" | "activated";

interface ButtonView {
  button: Button;
  hit: HTMLElement;
  overlays: Partial<Record<VisualState, HTMLImageElement>>;
}

export class MenuPlayer {
  private state: NavState = initialState();
  private readonly root: HTMLElement;
  private readonly stage: HTMLElement;
  private readonly playback: PlaybackAdapter;
  private readonly input: InputAdapter;
  private readonly resizeObserver: ResizeObserver;
  private renderedMenu?: string;
  private buttonViews = new Map<string, ButtonView>();
  private activatedButton?: string;
  private busy = false;
  private toastTimer?: ReturnType<typeof setTimeout>;
  private fallbackEl?: HTMLElement;
  private fallbackQueue?: { items: PlayItem[]; focusIndex: number };

  constructor(
    container: HTMLElement,
    private readonly options: PlayerOptions,
  ) {
    injectStyles(container.ownerDocument);
    this.root = document.createElement("div");
    this.root.className = "ms-root";
    this.root.setAttribute("role", "application");
    this.root.setAttribute("aria-label", options.manifest.title.name);
    this.stage = document.createElement("div");
    this.stage.className = "ms-stage";
    this.root.appendChild(this.stage);
    container.appendChild(this.root);

    this.playback = (options.playback ?? ((el) => new Html5Playback(el)))(this.root);
    this.playback.onEnded = () => this.onPlaybackEnded();
    this.playback.onStopped = (position) => this.onPlaybackStopped(position);
    this.playback.onError = (message) => {
      this.toast(message);
      this.onPlaybackStopped(0);
    };

    this.input = options.input ?? new KeyboardInput();
    this.input.attach((key) => this.onKey(key));

    this.resizeObserver = new ResizeObserver(() => this.layout());
    this.resizeObserver.observe(this.root);
    this.preloadImages();
  }

  start() {
    if (this.options.view === "fallback") this.showFallback();
    else this.dispatch({ type: "start" });
  }

  destroy() {
    this.input.detach();
    this.resizeObserver.disconnect();
    this.playback.destroy();
    clearTimeout(this.toastTimer);
    this.root.remove();
  }

  get navState(): NavState {
    return this.state;
  }

  /** Feed a navigator event (also used by hosts with custom input). Returns false while busy. */
  dispatch(event: NavEvent): boolean {
    if (this.busy) return false;
    const { state, effects } = step(this.options.manifest, this.state, event);
    const flash = effects.find((e): e is Extract<Effect, { type: "activated" }> => e.type === "activated");
    const apply = () => {
      this.state = state;
      this.render();
      for (const effect of effects) this.perform(effect);
      this.options.onStateChange?.(state);
    };
    const flashMs = this.options.activatedFlashMs ?? 180;
    if (flash && flashMs > 0 && flash.menu === this.renderedMenu) {
      this.busy = true;
      this.activatedButton = flash.button;
      this.updateButtons();
      setTimeout(() => {
        this.busy = false;
        this.activatedButton = undefined;
        apply();
      }, flashMs);
    } else {
      apply();
    }
    return true;
  }

  // --- input & playback -----------------------------------------------------------------------

  private onKey(key: Key): boolean {
    if (this.fallbackQueue || this.state.mode === "playing") {
      if (key !== "back") return false; // leave other keys to the video element
      this.onPlaybackStopped(this.playback.stop());
      return true;
    }
    if (this.fallbackEl) return this.onFallbackKey(key);
    if (this.state.mode !== "menu") return false;
    this.dispatch({ type: "press", key });
    return true;
  }

  private onPlaybackEnded() {
    if (this.fallbackQueue) return this.nextFallbackItem();
    this.dispatch({ type: "ended" });
  }

  private onPlaybackStopped(position: number) {
    if (this.fallbackQueue) {
      const focus = this.fallbackQueue.focusIndex;
      this.fallbackQueue = undefined;
      this.showFallback(focus);
      return;
    }
    this.dispatch({ type: "stopped", position });
  }

  private perform(effect: Effect) {
    switch (effect.type) {
      case "play":
        this.playMedia(effect.media, effect.start);
        break;
      case "exit":
        if (this.options.onExit) this.options.onExit();
        else this.toast("Top menu");
        break;
      case "activated":
        break;
    }
  }

  private playMedia(mediaId: string, start: number) {
    const { manifest, manifestUrl, resolvers } = this.options;
    const media = manifest.media[mediaId]!;
    const url = resolveMedia(media.ref, manifestUrl, resolvers);
    const label = media.label ?? manifest.title.name;
    if (!url) {
      this.toast(`Can't find "${label}".`);
      queueMicrotask(() => this.onPlaybackStopped(0));
      return;
    }
    this.playback.play(url, start, label);
  }

  // --- menu rendering --------------------------------------------------------------------------

  private currentMenu(): Menu | undefined {
    return this.state.menu ? this.options.manifest.menus?.[this.state.menu] : undefined;
  }

  private render() {
    if (this.state.mode === "fallback") {
      this.showFallback();
      return;
    }
    const menu = this.currentMenu();
    if (menu && this.state.menu !== this.renderedMenu) this.buildMenu(this.state.menu!, menu);
    this.stage.hidden = this.state.mode !== "menu";
    this.updateButtons();
  }

  private asset(path: string): string {
    return resolvePath(path, this.options.manifestUrl);
  }

  private buildMenu(id: string, menu: Menu) {
    this.stage.replaceChildren();
    this.buttonViews.clear();
    this.renderedMenu = id;
    this.stage.setAttribute("aria-label", menu.label ?? id);
    this.stage.style.background = menu.background?.color ?? "#000";

    if (menu.background?.image) {
      const bg = document.createElement("img");
      bg.className = "ms-bg";
      bg.alt = "";
      bg.src = this.asset(menu.background.image);
      this.stage.appendChild(bg);
    }

    const { width, height } = menu.canvas;
    const place = (el: HTMLElement, [x, y, w, h]: readonly number[]) => {
      el.style.left = `${(x! / width) * 100}%`;
      el.style.top = `${(y! / height) * 100}%`;
      el.style.width = `${(w! / width) * 100}%`;
      el.style.height = `${(h! / height) * 100}%`;
    };

    for (const button of menu.buttons) {
      const overlays: ButtonView["overlays"] = {};
      for (const name of ["normal", "selected", "activated"] as const) {
        const s = button.states?.[name];
        if (!s?.image) continue;
        const img = document.createElement("img");
        img.className = "ms-overlay";
        img.alt = "";
        img.src = this.asset(s.image);
        img.hidden = true;
        place(img, s.rect ?? button.rect);
        this.stage.appendChild(img);
        overlays[name] = img;
      }

      const hit = document.createElement("div");
      hit.className = "ms-button";
      hit.tabIndex = -1;
      hit.setAttribute("role", "button");
      hit.setAttribute("aria-label", button.a11yLabel ?? button.label ?? button.id);
      hit.dataset.button = button.id;
      place(hit, button.rect);
      if (button.showLabel) {
        hit.classList.add("ms-labelled");
        hit.textContent = button.label ?? "";
      }
      // Without highlight art the viewer couldn't see focus, so draw a ring.
      if (!overlays.selected && !button.showLabel) hit.classList.add("ms-ring");
      hit.addEventListener("mouseenter", () => this.dispatch({ type: "select", button: button.id }));
      hit.addEventListener("click", () => this.dispatch({ type: "activate", button: button.id }));
      this.stage.appendChild(hit);
      this.buttonViews.set(button.id, { button, hit, overlays });
    }
    this.layout();
  }

  private updateButtons() {
    for (const [id, view] of this.buttonViews) {
      const visual: VisualState =
        id === this.activatedButton ? "activated" : id === this.state.focus ? "selected" : "normal";
      for (const [name, img] of Object.entries(view.overlays)) img.hidden = name !== visual;
      view.hit.classList.toggle("ms-selected", visual === "selected");
      view.hit.classList.toggle("ms-activated", visual === "activated");
      if (view.button.showLabel) this.applyTextStyle(view, view.button.states?.[visual]);
      if (visual === "selected" && this.state.mode === "menu") view.hit.focus({ preventScroll: true });
    }
  }

  private applyTextStyle(view: ButtonView, s: ButtonState | undefined) {
    view.hit.style.color = s?.textColor ?? "";
    view.hit.style.background = s?.fill ?? "";
  }

  /** Fit the stage to the root at the canvas's display aspect (letterbox/pillarbox). */
  private layout() {
    const menu = this.currentMenu();
    if (!menu) return;
    const aspect = canvasAspect(menu);
    const w = this.root.clientWidth;
    const h = this.root.clientHeight;
    const stageW = w / h > aspect ? h * aspect : w;
    const stageH = stageW / aspect;
    Object.assign(this.stage.style, {
      width: `${stageW}px`,
      height: `${stageH}px`,
      left: `${(w - stageW) / 2}px`,
      top: `${(h - stageH) / 2}px`,
    });
    for (const view of this.buttonViews.values())
      if (view.button.showLabel) view.hit.style.fontSize = `${(view.button.rect[3] / menu.canvas.height) * stageH * 0.5}px`;
  }

  private preloadImages() {
    const paths = new Set<string>();
    for (const menu of Object.values(this.options.manifest.menus ?? {})) {
      if (menu.background?.image) paths.add(menu.background.image);
      for (const b of menu.buttons)
        for (const s of Object.values(b.states ?? {})) if (s?.image) paths.add(s.image);
    }
    for (const p of paths) new Image().src = this.asset(p);
  }

  private toast(message: string) {
    let el = this.root.querySelector<HTMLElement>(".ms-toast");
    if (!el) {
      el = document.createElement("div");
      el.className = "ms-toast";
      el.setAttribute("role", "status");
      this.root.appendChild(el);
    }
    el.textContent = message;
    el.style.opacity = "1";
    clearTimeout(this.toastTimer);
    this.toastTimer = setTimeout(() => (el!.style.opacity = "0"), 2500);
  }

  // --- Level 0: fallback list ---------------------------------------------------------------------

  private showFallback(focusIndex = 0) {
    const { manifest, showHidden } = this.options;
    this.stage.hidden = true;
    this.fallbackEl?.remove();
    const el = document.createElement("div");
    el.className = "ms-fallback";
    const h1 = document.createElement("h1");
    h1.textContent = manifest.title.year ? `${manifest.title.name} (${manifest.title.year})` : manifest.title.name;
    el.appendChild(h1);
    const buttons: HTMLButtonElement[] = [];
    for (const group of manifest.fallback) {
      if (group.hidden && !showHidden) continue;
      const h2 = document.createElement("h2");
      h2.textContent = group.label;
      el.appendChild(h2);
      for (const item of group.items) {
        const index = buttons.length;
        const b = document.createElement("button");
        b.textContent = item.label;
        b.addEventListener("click", () => this.playFallbackItem(item, index));
        b.addEventListener("mouseenter", () => b.focus());
        el.appendChild(b);
        buttons.push(b);
      }
    }
    this.root.appendChild(el);
    this.fallbackEl = el;
    buttons[Math.min(focusIndex, buttons.length - 1)]?.focus();
  }

  private onFallbackKey(key: Key): boolean {
    const buttons = [...this.fallbackEl!.querySelectorAll("button")];
    const i = buttons.indexOf(document.activeElement as HTMLButtonElement);
    if (key === "up" || key === "down") {
      const next = buttons[Math.max(0, Math.min(buttons.length - 1, (i < 0 ? 0 : i) + (key === "up" ? -1 : 1)))];
      next?.focus();
      return true;
    }
    if (key === "enter") {
      buttons[i < 0 ? 0 : i]?.click();
      return true;
    }
    if (key === "back") {
      if (this.options.view !== "fallback" && this.options.manifest.menus && this.options.manifest.rootMenu) {
        this.fallbackEl!.remove();
        this.fallbackEl = undefined;
        this.dispatch({ type: "start" });
      } else if (this.options.onExit) this.options.onExit();
      return true;
    }
    return false;
  }

  private playFallbackItem(item: FallbackItem, focusIndex: number) {
    const action = item.action;
    const items = action.type === "play" ? [action] : action.type === "playAll" ? action.items : [];
    if (items.length === 0) return;
    this.fallbackEl?.remove();
    this.fallbackEl = undefined;
    this.fallbackQueue = { items: [...items], focusIndex };
    this.nextFallbackItem();
  }

  private nextFallbackItem() {
    const queue = this.fallbackQueue!;
    const item = queue.items.shift();
    if (!item) {
      this.fallbackQueue = undefined;
      this.showFallback(queue.focusIndex);
      return;
    }
    this.playMedia(item.media, startTime(this.options.manifest, item));
  }
}
