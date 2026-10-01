import {
  canvasAspect,
  initialState,
  MENU_KEYS,
  overlayOf,
  resolveMedia,
  resolvePath,
  startTime,
  step,
  type Button,
  type ButtonState,
  type CueWatch,
  type Effect,
  type FallbackItem,
  type Key,
  type Manifest,
  type MediaResolver,
  type Menu,
  type NavEvent,
  type NavState,
  type PlayItem,
} from "@openvideomenu/core";
import { Html5Playback, KeyboardInput, type InputAdapter, type PlaybackAdapter, type SegmentRequest } from "./adapters.js";
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
  /**
   * Level 2: an overlay's buttons started (true) or stopped (false) showing during playback. Native
   * hosts show the page over their video while one shows and send it the arrows and Enter.
   */
  onOverlay?: (showing: boolean) => void;
  /** How long the activated state shows before the action runs (ms). */
  activatedFlashMs?: number;
  /** Where the viewer last stopped (e.g. the server's saved resume point), used by "resume" actions. */
  initialLastStop?: { media: string; position: number };
  /**
   * Level 2: what the host knows of the viewer's setup, as "@" player state (e.g. "@audioLanguage",
   * "@region"). Merged over the defaults: the browser's language for the three language preferences.
   */
  player?: Record<string, number | string>;
}

/** The browser's (or TV's) language as the default preference for audio, subtitles and menus. */
function defaultPlayerState(): Record<string, string> {
  // A BCP 47 tag such as "fr-CA": the language and, when given, the country.
  const [language = "", region = ""] = (typeof navigator !== "undefined" ? navigator.language : "").split("-");
  const lang = language.length === 2 ? language.toLowerCase() : "";
  const state: Record<string, string> = lang ? { "@audioLanguage": lang, "@subtitleLanguage": lang, "@menuLanguage": lang } : {};
  if (/^[A-Za-z]{2}$/.test(region)) state["@country"] = region.toUpperCase();
  return state;
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
  /** Level 2: buttons over the video during playback (an overlay cue), drawn like a menu's. */
  private readonly overlayStage: HTMLElement;
  private renderedOverlay?: string;
  private overlayShown = false;
  private overlayViews = new Map<string, ButtonView>();
  private activatedButton?: string;
  private busy = false;
  private toastTimer?: ReturnType<typeof setTimeout>;
  private fallbackEl?: HTMLElement;
  private fallbackQueue?: { items: PlayItem[]; focusIndex: number };
  private bgVideo?: HTMLVideoElement;
  private bgAudio?: HTMLAudioElement;
  private readonly transitionVideo: HTMLVideoElement;
  private introTimer?: ReturnType<typeof setTimeout>;
  /** The countdown of a menu's timeout (Level 2), started by a startTimer effect. */
  private menuTimer?: ReturnType<typeof setTimeout>;
  private introFrame?: number;
  /** Browsers block autoplay with sound until the first input; menus start muted and unmute then. */
  private soundUnlocked = false;

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
    this.transitionVideo = document.createElement("video");
    this.transitionVideo.className = "ms-transition";
    this.transitionVideo.playsInline = true;
    this.transitionVideo.hidden = true;
    this.transitionVideo.addEventListener("ended", () => this.dispatch({ type: "transitionEnded" }));
    this.transitionVideo.addEventListener("error", () => {
      if (!this.transitionVideo.hidden) this.dispatch({ type: "transitionEnded" });
    });
    this.root.appendChild(this.transitionVideo);
    container.appendChild(this.root);
    if (options.initialLastStop) this.state = { ...this.state, lastStop: options.initialLastStop };

    this.playback = (options.playback ?? ((el) => new Html5Playback(el)))(this.root);
    this.playback.onEnded = () => this.onPlaybackEnded();
    this.playback.onStopped = (position) => this.onPlaybackStopped(position);
    this.playback.onError = (message) => {
      this.toast(message);
      this.onPlaybackStopped(0);
    };
    // Cue reports can arrive during a button's activated flash; retry until the navigator takes them.
    const report = (event: NavEvent) => {
      if (!this.dispatch(event)) setTimeout(() => report(event), 50);
    };
    this.playback.onCue = (id) => report({ type: "cue", id });
    this.playback.onCueEnd = (id) => report({ type: "cueEnd", id });

    // Above the video (added by the playback adapter), so overlay buttons show over it.
    this.overlayStage = document.createElement("div");
    this.overlayStage.className = "ms-stage ms-overlay-stage";
    this.overlayStage.hidden = true;
    this.root.appendChild(this.overlayStage);

    this.input = options.input ?? new KeyboardInput();
    this.input.attach((key, position) => this.onKey(key, position));

    // Pause menu music/motion while the page is hidden (browser tab switched, TV app sent to the
    // background with Home) and pick up where it was when it comes back.
    document.addEventListener("visibilitychange", this.onVisibility);

    this.resizeObserver = new ResizeObserver(() => this.layout());
    this.resizeObserver.observe(this.root);
    this.preloadImages();
  }

  start() {
    if (this.options.view === "fallback") this.showFallback();
    else
      this.dispatch({
        type: "start",
        player: { ...defaultPlayerState(), ...(this.options.player ?? {}) },
        seed: 1 + Math.floor(Math.random() * 2147483646),
      });
  }

  destroy() {
    this.input.detach();
    document.removeEventListener("visibilitychange", this.onVisibility);
    this.resizeObserver.disconnect();
    this.stopIntroWatch();
    this.stopBackground();
    this.transitionVideo.pause();
    this.playback.destroy();
    clearTimeout(this.toastTimer);
    clearTimeout(this.menuTimer);
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
    // A cue that opens a menu leaves playback without the player reporting a stop: stop it here.
    const leftPlayback = this.state.mode === "playing" && state.mode !== "playing" && event.type !== "ended" && event.type !== "stopped";
    const apply = () => {
      const prev = this.state;
      this.state = state;
      if (leftPlayback) this.playback.stop();
      this.render(prev);
      if (state.mode !== "menu") clearTimeout(this.menuTimer); // the countdown belongs to a menu on screen
      for (const effect of effects) this.perform(effect);
      this.options.onStateChange?.(state);
    };
    const flashMs = this.options.activatedFlashMs ?? 180;
    const flashes = !!flash && ("menu" in flash ? flash.menu === this.renderedMenu : flash.cue === this.renderedOverlay);
    if (flash && flashMs > 0 && flashes) {
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

  private onKey(key: Key, position?: number): boolean {
    this.unlockSound();
    // Level 2: the menu keys belong to the menus, also during playback, where they say where it was.
    if ((MENU_KEYS as readonly string[]).includes(key)) {
      if (this.fallbackQueue || this.fallbackEl || (this.state.mode !== "menu" && this.state.mode !== "playing")) return false;
      const at = this.state.mode === "playing" ? (position ?? this.playback.currentPosition?.()) : undefined;
      this.dispatch({ type: "press", key, ...(at !== undefined ? { position: at } : {}) });
      return true;
    }
    // While an overlay's buttons show, the arrows and Enter are theirs (Back still stops playback).
    if (this.state.mode === "playing" && this.state.overlay && key !== "back") {
      this.dispatch({ type: "press", key });
      return true;
    }
    if (this.fallbackQueue || this.state.mode === "playing") {
      if (key !== "back") return false; // leave other keys to the video element
      this.onPlaybackStopped(this.playback.stop());
      return true;
    }
    if (this.fallbackEl) return this.onFallbackKey(key);
    if (this.state.mode !== "menu" && this.state.mode !== "transition") return false;
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
        this.lastPlay = effect;
        this.playMedia(effect.media, effect.start, effect.audio, effect.subtitle, effect.angle, effect.end, effect.cues);
        break;
      case "seek":
        this.playback.seek?.(effect.position);
        break;
      case "tracks":
        this.changeTracks(effect);
        break;
      case "exit":
        if (this.options.onExit) this.options.onExit();
        else this.toast("Top menu");
        break;
      case "transition":
        this.playTransition(effect.src);
        break;
      case "skipIntro":
        this.seekBackground(this.currentMenu()?.buttonsAt ?? 0);
        break;
      case "startTimer":
        this.startMenuTimer(effect.menu, effect.after);
        break;
      case "activated":
        break;
      case "budgetExceeded":
        // A named action ran itself without end; the navigator dropped the rest. The menus carry on.
        console.warn("Open Video Menu: an action loop reached the step budget and was stopped.");
        break;
    }
  }

  /** The play effect of what plays now, for changing its tracks (Level 2). */
  private lastPlay?: Extract<Effect, { type: "play" }>;

  /**
   * Level 2: the menus changed a track during playback. A soundtrack switches in place where the
   * playback adapter can; otherwise the title starts again where it is, with the new tracks.
   */
  private changeTracks(change: Extract<Effect, { type: "tracks" }>) {
    const last = this.lastPlay;
    if (!last || this.state.mode !== "playing") return;
    const next = {
      ...last,
      ...(change.audio !== undefined ? { audio: change.audio } : {}),
      ...(change.subtitle !== undefined ? { subtitle: change.subtitle } : {}),
      ...(change.angle !== undefined ? { angle: change.angle } : {}),
    };
    this.lastPlay = next;
    if (change.audio !== undefined && change.subtitle === undefined && change.angle === undefined && this.playback.switchAudio?.(change.audio)) return;
    const at = this.playback.currentPosition?.() ?? change.at;
    this.playMedia(next.media, at, next.audio, next.subtitle, next.angle, next.end, next.cues);
  }

  /** (Re)start a menu's countdown; when it runs out, tell the navigator (which ignores stale ones). */
  private startMenuTimer(menu: string, after: number) {
    clearTimeout(this.menuTimer);
    const fire = () => {
      // Busy for a moment (a button's activated flash): try again shortly rather than lose it.
      if (!this.dispatch({ type: "timeout", menu })) this.menuTimer = setTimeout(fire, 200);
    };
    this.menuTimer = setTimeout(fire, after * 1000);
  }

  private playMedia(mediaId: string, start: number, audio?: number, subtitle?: number, angle?: number, end?: number, cues?: CueWatch[]) {
    const { manifest } = this.options;
    const media = manifest.media[mediaId]!;
    const common = {
      start,
      label: media.label ?? manifest.title.name,
      ...(audio !== undefined ? { audio } : {}),
      ...(subtitle !== undefined ? { subtitle } : {}),
      ...(end !== undefined ? { end } : {}),
      ...(cues?.length ? { cues } : {}),
    };
    // Level 2: made of segments, each a range of a file (with the play's angle where it has angles).
    if (media.segments) {
      const segments: SegmentRequest[] = media.segments.map((seg) => ({
        mediaId: seg.media,
        ...this.source(seg.media, angle),
        start: seg.start ?? 0,
        ...(seg.end !== undefined ? { end: seg.end } : {}),
      }));
      this.playback.play({ mediaId, segments, ...(angle !== undefined ? { angle } : {}), ...common });
      return;
    }
    this.playback.play({ mediaId, ...this.source(mediaId, angle), ...common });
  }

  /**
   * A media entry's file for a play: angle 2 and up of angle files is its own file; angle tracks are
   * chosen inside the one file. An angle the media doesn't have plays angle 1.
   */
  private source(mediaId: string, angle: number | undefined): Pick<SegmentRequest, "ref" | "url" | "angle" | "videoTrack"> {
    const { manifest, manifestUrl, resolvers } = this.options;
    const media = manifest.media[mediaId]!;
    const count = media.angles ? 1 + media.angles.length : (media.angleTracks ?? 1);
    const a = count > 1 && angle !== undefined ? (angle <= count ? angle : 1) : undefined;
    const ref = a !== undefined && a > 1 && media.angles ? media.angles[a - 2]! : media.ref!;
    return {
      ref,
      url: resolveMedia(ref, manifestUrl, resolvers),
      ...(a !== undefined ? { angle: a } : {}),
      ...(a !== undefined && media.angleTracks ? { videoTrack: a } : {}),
    };
  }

  // --- menu rendering --------------------------------------------------------------------------

  private currentMenu(): Menu | undefined {
    return this.state.menu ? this.options.manifest.menus?.[this.state.menu] : undefined;
  }

  private render(prev: NavState) {
    const s = this.state;
    if (s.mode === "fallback") {
      this.stopBackground();
      this.showFallback();
      return;
    }
    const menu = this.currentMenu();
    const menuChanged = !!menu && s.menu !== this.renderedMenu;
    if (menuChanged) this.buildMenu(s.menu!, menu!);
    this.stage.hidden = s.mode !== "menu";
    // While a title plays, the page is see-through: a native host's video shows behind overlay buttons.
    this.root.classList.toggle("ms-playing", s.mode === "playing");
    if (s.mode !== "transition") this.stopTransition();

    // Background motion/audio runs only while the menu is on screen. A fresh entry plays the intro
    // from 0; returning (back, end of playback) starts at the loop point: the intro is not replayed.
    const entered = s.mode === "menu" && (prev.mode !== "menu" || menuChanged || prev.menu !== s.menu);
    if (entered && menu) {
      this.startBackground(menu, s.intro);
      if (!s.buttonsReady) this.watchIntro(menu);
    } else if (s.mode !== "menu") {
      this.stopBackground();
    }
    if (s.buttonsReady) this.stopIntroWatch();
    this.stage.classList.toggle("ms-intro", s.mode === "menu" && !s.buttonsReady);
    this.renderOverlay();
    this.updateButtons();
  }

  /** Level 2: the overlay showing during playback, if any, over the video. */
  private renderOverlay() {
    const overlay = this.state.mode === "playing" ? overlayOf(this.options.manifest, this.state) : undefined;
    const id = overlay ? this.state.overlay!.cue : undefined;
    if (id !== this.renderedOverlay || (overlay && this.overlayStage.hidden)) {
      this.overlayStage.replaceChildren();
      this.overlayViews.clear();
      this.renderedOverlay = id;
      if (overlay) {
        this.buildButtons(this.overlayStage, overlay.canvas, overlay.buttons, this.overlayViews);
        this.overlayStage.hidden = false;
        this.layout();
      }
    }
    this.overlayStage.hidden = !overlay;
    if (!!overlay !== this.overlayShown) {
      this.overlayShown = !!overlay;
      this.options.onOverlay?.(this.overlayShown);
    }
  }

  // --- Motion: background loops, intro timing, transitions -----------------------------------

  private startBackground(menu: Menu, fromIntro: boolean) {
    const bg = menu.background;
    const pairs: [HTMLMediaElement | undefined, { loopStart?: number } | undefined][] = [
      [this.bgVideo, bg?.video],
      [this.bgAudio, bg?.audio],
    ];
    for (const [el, spec] of pairs) {
      if (!el || !spec) continue;
      el.currentTime = fromIntro ? 0 : (spec.loopStart ?? 0);
      this.playWithAutoplayFallback(el);
    }
  }

  private readonly onVisibility = () => {
    if (document.hidden) {
      this.stopBackground();
      if (!this.transitionVideo.hidden) this.transitionVideo.pause();
      // Nothing may start while nobody's watching (the TV app in the background).
      clearTimeout(this.menuTimer);
    } else if (this.state.mode === "menu") {
      for (const el of [this.bgVideo, this.bgAudio]) if (el) this.playWithAutoplayFallback(el);
      // Back on screen: the countdown starts over, as if the menu had just appeared.
      const timeout = this.currentMenu()?.timeout;
      if (timeout && this.state.menu) this.startMenuTimer(this.state.menu, timeout.after);
    } else if (this.state.mode === "transition") {
      this.playWithAutoplayFallback(this.transitionVideo);
    }
  };

  private stopBackground() {
    this.bgVideo?.pause();
    this.bgAudio?.pause();
  }

  private seekBackground(time: number) {
    const menu = this.currentMenu();
    if (this.bgVideo && menu?.background?.video) this.bgVideo.currentTime = time;
    if (this.bgAudio && menu?.background?.audio) this.bgAudio.currentTime = time;
  }

  /**
   * Play a background element with sound. Browsers often refuse unmuted autoplay until the viewer
   * interacts; then play muted and unmute on the first input. Native hosts (TV apps) allow sound at once.
   */
  private playWithAutoplayFallback(el: HTMLMediaElement) {
    el.muted = false;
    void el.play().catch(() => {
      el.muted = true;
      void el.play().catch(() => {});
    });
  }

  private unlockSound() {
    if (this.soundUnlocked) return;
    this.soundUnlocked = true;
    for (const el of [this.bgVideo, this.bgAudio, this.transitionVideo]) if (el) el.muted = false;
  }

  /** Wait for the intro to reach buttonsAt: on the background video's clock, or a timer without one. */
  private watchIntro(menu: Menu) {
    this.stopIntroWatch();
    const at = menu.buttonsAt ?? 0;
    const video = menu.background?.video ? this.bgVideo : undefined;
    if (video) {
      const tick = () => {
        this.introFrame = undefined;
        const due = video.currentTime >= at || video.ended;
        // dispatch() refuses while a button flash is running; keep polling until it is accepted.
        if (!due || !this.dispatch({ type: "buttonsReady" })) this.introFrame = requestAnimationFrame(tick);
      };
      this.introFrame = requestAnimationFrame(tick);
    } else {
      const fire = () => {
        if (!this.dispatch({ type: "buttonsReady" })) this.introTimer = setTimeout(fire, 50);
      };
      this.introTimer = setTimeout(fire, at * 1000);
    }
  }

  private stopIntroWatch() {
    clearTimeout(this.introTimer);
    if (this.introFrame !== undefined) cancelAnimationFrame(this.introFrame);
    this.introTimer = undefined;
    this.introFrame = undefined;
  }

  private playTransition(src: string) {
    const v = this.transitionVideo;
    this.stopBackground();
    v.hidden = false;
    v.src = this.asset(src);
    this.playWithAutoplayFallback(v);
  }

  private stopTransition() {
    const v = this.transitionVideo;
    if (v.hidden) return;
    v.pause();
    v.hidden = true;
    v.removeAttribute("src");
    v.load();
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

    this.stopBackground();
    this.bgVideo = undefined;
    this.bgAudio = undefined;
    if (menu.background?.image) {
      const bg = document.createElement("img");
      bg.className = "ms-bg";
      bg.alt = "";
      bg.src = this.asset(menu.background.image);
      this.stage.appendChild(bg);
    }
    const looping = <T extends HTMLMediaElement>(el: T, spec: { src: string; loopStart?: number }): T => {
      el.src = this.asset(spec.src);
      el.preload = "auto";
      // Plays once from 0 (the intro), then loops from loopStart.
      el.addEventListener("ended", () => {
        el.currentTime = spec.loopStart ?? 0;
        void el.play().catch(() => {});
      });
      return el;
    };
    if (menu.background?.video) {
      const v = looping(document.createElement("video"), menu.background.video);
      v.className = "ms-bg";
      v.playsInline = true;
      if (menu.background.image) v.poster = this.asset(menu.background.image);
      this.stage.appendChild(v);
      this.bgVideo = v;
    }
    if (menu.background?.audio) {
      this.bgAudio = looping(document.createElement("audio"), menu.background.audio);
      this.stage.appendChild(this.bgAudio);
    }

    this.buildButtons(this.stage, menu.canvas, menu.buttons, this.buttonViews);
    this.layout();
  }

  /** Buttons with their state images and labels, placed on a canvas (a menu's, or an overlay's). */
  private buildButtons(parent: HTMLElement, canvas: { width: number; height: number }, buttons: Button[], views: Map<string, ButtonView>) {
    const { width, height } = canvas;
    const place = (el: HTMLElement, [x, y, w, h]: readonly number[]) => {
      el.style.left = `${(x! / width) * 100}%`;
      el.style.top = `${(y! / height) * 100}%`;
      el.style.width = `${(w! / width) * 100}%`;
      el.style.height = `${(h! / height) * 100}%`;
    };

    for (const button of buttons) {
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
        parent.appendChild(img);
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
        const text = document.createElement("span");
        text.textContent = button.label ?? "";
        hit.appendChild(text);
      }
      // Without highlight art the viewer couldn't see focus, so draw a ring.
      if (!overlays.selected && !button.showLabel) hit.classList.add("ms-ring");
      hit.addEventListener("mouseenter", () => this.dispatch({ type: "select", button: button.id }));
      hit.addEventListener("click", () => this.dispatch({ type: "activate", button: button.id }));
      parent.appendChild(hit);
      views.set(button.id, { button, hit, overlays });
    }
  }

  private updateButtons() {
    const menuFocus = this.state.mode === "menu" ? this.state.focus : undefined;
    const overlayFocus = this.state.mode === "playing" ? this.state.overlay?.focus : undefined;
    for (const [views, focus, focusable] of [
      [this.buttonViews, this.state.focus, !!menuFocus],
      [this.overlayViews, overlayFocus, !!overlayFocus],
    ] as const) {
      for (const [id, view] of views) {
        const visual: VisualState = id === this.activatedButton ? "activated" : id === focus ? "selected" : "normal";
        for (const [name, img] of Object.entries(view.overlays)) img.hidden = name !== visual;
        view.hit.classList.toggle("ms-selected", visual === "selected");
        view.hit.classList.toggle("ms-activated", visual === "activated");
        if (view.button.showLabel) this.applyTextStyle(view, view.button.states?.[visual]);
        if (visual === "selected" && focusable) view.hit.focus({ preventScroll: true });
      }
    }
  }

  private applyTextStyle(view: ButtonView, s: ButtonState | undefined) {
    view.hit.style.color = s?.textColor ?? "";
    view.hit.style.background = s?.fill ?? "";
  }

  /** Fit the stages to the root at their canvas's display aspect (letterbox/pillarbox). */
  private layout() {
    const menu = this.currentMenu();
    if (menu) this.fit(this.stage, menu, this.buttonViews);
    const overlay = this.state.mode === "playing" ? overlayOf(this.options.manifest, this.state) : undefined;
    if (overlay) this.fit(this.overlayStage, overlay, this.overlayViews);
  }

  private fit(stage: HTMLElement, owner: { canvas: Menu["canvas"] }, views: Map<string, ButtonView>) {
    const aspect = canvasAspect(owner as Menu);
    const w = this.root.clientWidth;
    const h = this.root.clientHeight;
    const stageW = w / h > aspect ? h * aspect : w;
    const stageH = stageW / aspect;
    Object.assign(stage.style, {
      width: `${stageW}px`,
      height: `${stageH}px`,
      left: `${(w - stageW) / 2}px`,
      top: `${(h - stageH) / 2}px`,
    });
    // Labels are half the button's height, smaller when the text would be wider than the button.
    for (const view of views.values()) {
      if (!view.button.showLabel) continue;
      const size = (view.button.rect[3] / owner.canvas.height) * stageH * 0.5;
      view.hit.style.fontSize = `${size}px`;
      const room = view.hit.clientWidth * 0.94;
      const width = (view.hit.firstElementChild as HTMLElement | null)?.getBoundingClientRect().width ?? 0;
      if (width > room && room > 0) view.hit.style.fontSize = `${Math.max((size * room) / width, 8)}px`;
    }
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
    // The fallback list plays without the navigator: no cues, but an item's end still stops it.
    this.playMedia(item.media, startTime(this.options.manifest, item), undefined, undefined, undefined, item.end);
  }
}
