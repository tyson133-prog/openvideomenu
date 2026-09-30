import type { CueWatch, Key, MediaRef } from "@openvideomenu/core";

/** What the renderer asks a playback adapter to play. */
export interface PlayRequest {
  /** Manifest media id. */
  mediaId: string;
  /** The media's ref exactly as written in the manifest (hosts that match files themselves use this). */
  ref: MediaRef;
  /** Resolved URL for string refs and known schemes; undefined when only the host can resolve it. */
  url?: string;
  /** Seconds. */
  start: number;
  /** Level 2: 1-based audio track; undefined = player default. */
  audio?: number;
  /** Level 2: 1-based subtitle track, 0 = off; undefined = player default. */
  subtitle?: number;
  /**
   * Level 2, media with angles (spec §4.1): which angle, 1-based. For angle files, `ref` and `url` are
   * already that angle's file; hosts that match files by media id pick the matching angle file.
   */
  angle?: number;
  /** Level 2: for media whose angles are video tracks, the 1-based video track to show. */
  videoTrack?: number;
  /** Level 2: stop here (seconds) and report onEnded, as if the item ended (spec 6.3). */
  end?: number;
  /** Level 2: cues to report with onCue / onCueEnd while this plays (spec 6.3). */
  cues?: CueWatch[];
  label: string;
}

/**
 * Plays media for the renderer. Hosts replace the default HTML5 adapter to route playback through
 * their own player (Jellyfin's, a native bridge, a DRM-capable player...).
 */
export interface PlaybackAdapter {
  /** Start playing. The adapter reports the outcome through onEnded / onStopped / onError. */
  play(request: PlayRequest): void;
  /** Stop early; returns the position in seconds that was reached. */
  stop(): number;
  /** Called by the adapter when the item finishes on its own. */
  onEnded?: () => void;
  /** Called by the adapter when the viewer stops playback (e.g. with the player's own back button). */
  onStopped?: (position: number) => void;
  /** Called when the media can't be played. */
  onError?: (message: string) => void;
  /** Level 2: playback passed a point cue's time, or entered an overlay's window (see CueClock). */
  onCue?: (id: string) => void;
  /** Level 2: playback left an overlay's window. */
  onCueEnd?: (id: string) => void;
  destroy(): void;
}

/**
 * Level 2: turns playback positions into cue reports (spec 6.3), for any adapter that can read its
 * position. Point cues fire when playback passes their time in ordinary play (not at the start
 * position, not when seeking past); overlays are reported while the position is inside their window,
 * however it got there; `end` reports the item as ended.
 */
export class CueClock {
  private last?: number;
  /** Where playback was asked to start: readings before it (a player that starts at the keyframe before) are ignored. */
  private floor?: number;
  private readonly inside = new Set<string>();

  /** A jump larger than this between two readings is a seek, not play. */
  static readonly SEEK_SECONDS = 2;

  constructor(
    private readonly cues: CueWatch[],
    private readonly end: number | undefined,
    private readonly report: { cue(id: string): void; cueEnd(id: string): void; ended(): void },
  ) {}

  /** Where playback starts: a cue exactly there doesn't fire, nor any before it the player shows first. */
  start(position: number) {
    this.floor = position;
    this.last = position;
  }

  /** Feed the current position (seconds). Returns true once `end` is reached. */
  tick(position: number): boolean {
    if (this.floor !== undefined) {
      if (position < this.floor) return false;
      this.floor = undefined;
    }
    const last = this.last;
    this.last = position;
    const played = last !== undefined && position >= last && position - last <= CueClock.SEEK_SECONDS;
    for (const c of this.cues) {
      if ("at" in c) {
        if (played && last! < c.at && c.at <= position) this.report.cue(c.id);
      } else {
        const inWindow = c.from <= position && position < c.to;
        if (inWindow && !this.inside.has(c.id)) {
          this.inside.add(c.id);
          this.report.cue(c.id);
        } else if (!inWindow && this.inside.has(c.id)) {
          this.inside.delete(c.id);
          this.report.cueEnd(c.id);
        }
      }
    }
    if (this.end !== undefined && position >= this.end) {
      this.report.ended();
      return true;
    }
    return false;
  }
}

/**
 * Turns host input into navigator keys. The default keyboard adapter covers keyboards and
 * most TV remotes in browsers (arrow keys, Enter/OK, Back/Escape/Backspace).
 */
export interface InputAdapter {
  attach(onKey: (key: Key) => boolean): void;
  detach(): void;
}

const KEY_MAP: Record<string, Key> = {
  ArrowUp: "up",
  ArrowDown: "down",
  ArrowLeft: "left",
  ArrowRight: "right",
  Enter: "enter",
  " ": "enter",
  Escape: "back",
  Backspace: "back",
  GoBack: "back", // Android TV / some TV browsers
  BrowserBack: "back",
};

export class KeyboardInput implements InputAdapter {
  private handler?: (e: KeyboardEvent) => void;

  constructor(private readonly target: EventTarget = window) {}

  attach(onKey: (key: Key) => boolean) {
    this.handler = (e: KeyboardEvent) => {
      const key = KEY_MAP[e.key] ?? (e.keyCode === 461 ? "back" : undefined); // 461: LG webOS Back
      if (key && onKey(key)) e.preventDefault();
    };
    this.target.addEventListener("keydown", this.handler as EventListener);
  }

  detach() {
    if (this.handler) this.target.removeEventListener("keydown", this.handler as EventListener);
  }
}

/** Default playback: an HTML5 <video> element layered over the menu. */
export class Html5Playback implements PlaybackAdapter {
  onEnded?: () => void;
  onStopped?: (position: number) => void;
  onError?: (message: string) => void;
  onCue?: (id: string) => void;
  onCueEnd?: (id: string) => void;
  private readonly video: HTMLVideoElement;
  private clock?: CueClock;

  constructor(container: HTMLElement) {
    this.video = document.createElement("video");
    this.video.className = "ms-video";
    this.video.controls = true;
    this.video.playsInline = true;
    this.video.hidden = true;
    container.appendChild(this.video);
    this.video.addEventListener("ended", () => {
      this.hide();
      this.onEnded?.();
    });
    this.video.addEventListener("timeupdate", () => this.clock?.tick(this.video.currentTime));
    this.video.addEventListener("error", () => {
      if (this.video.hidden) return;
      const code = this.video.error?.code;
      this.hide();
      this.onError?.(
        code === MediaError.MEDIA_ERR_SRC_NOT_SUPPORTED
          ? "This file is missing or in a format this browser can't play."
          : "Playback failed.",
      );
    });
  }

  play({ url, start, label, audio, end, cues }: PlayRequest) {
    const v = this.video;
    this.pendingAudio = audio;
    this.clock = undefined;
    if (end !== undefined || cues?.length) {
      const clock: CueClock = new CueClock(cues ?? [], end, {
        cue: (id) => this.onCue?.(id),
        cueEnd: (id) => this.onCueEnd?.(id),
        ended: () => {
          if (this.clock !== clock) return;
          this.clock = undefined;
          this.hide();
          this.onEnded?.();
        },
      });
      clock.start(start);
      this.clock = clock;
    }
    if (!url) {
      queueMicrotask(() => this.onError?.(`Can't find "${label}".`));
      return;
    }
    v.hidden = false;
    v.title = label;
    v.src = url;
    const seek = () => {
      if (start > 0) v.currentTime = start;
      this.applyAudioTrack();
      void v.play().catch(() => {
        /* autoplay may need a gesture; controls are visible */
      });
    };
    if (v.readyState >= HTMLMediaElement.HAVE_METADATA) seek();
    else v.addEventListener("loadedmetadata", seek, { once: true });
    v.focus({ preventScroll: true });
  }

  private pendingAudio?: number;

  /** Browsers expose audio tracks only in some engines (video.audioTracks); best effort elsewhere. */
  private applyAudioTrack() {
    const tracks = (this.video as HTMLVideoElement & { audioTracks?: { length: number; [i: number]: { enabled: boolean } } }).audioTracks;
    const want = this.pendingAudio;
    if (!tracks || want === undefined || want < 1 || want > tracks.length) return;
    for (let i = 0; i < tracks.length; i++) tracks[i]!.enabled = i === want - 1;
  }

  stop(): number {
    const position = this.video.currentTime || 0;
    this.hide();
    return position;
  }

  private hide() {
    this.clock = undefined;
    this.video.pause();
    this.video.hidden = true;
    this.video.removeAttribute("src");
    this.video.load();
  }

  destroy() {
    this.hide();
    this.video.remove();
  }
}
