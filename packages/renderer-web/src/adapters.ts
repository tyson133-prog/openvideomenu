import type { CueWatch, Key, MediaRef } from "@openvideomenu/core";

/** What the renderer asks a playback adapter to play. */
export interface PlayRequest {
  /** Manifest media id. */
  mediaId: string;
  /**
   * The media's ref exactly as written in the manifest (hosts that match files themselves use this).
   * Absent for media made of segments: see `segments`.
   */
  ref?: MediaRef;
  /** Resolved URL for string refs and known schemes; undefined when only the host can resolve it. */
  url?: string;
  /** Seconds (on the title's own timeline when it is made of segments). */
  start: number;
  /**
   * Level 2 (spec 4.2): the pieces to play in order as one title, each a range of a file. Positions,
   * `end`, cues and the reported stop are on the title's timeline: segment i starts at the sum of the
   * lengths (end - start) of the segments before it.
   */
  segments?: SegmentRequest[];
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

/** One piece of a title made of segments: a range of a media entry that has a file. */
export interface SegmentRequest {
  mediaId: string;
  ref: MediaRef;
  url?: string;
  /** Seconds into the file. */
  start: number;
  /** Seconds into the file; absent on a last segment that runs to the end of its file. */
  end?: number;
  /** For segment media with angles: which angle (ref/url are already that angle's file), or its video track. */
  angle?: number;
  videoTrack?: number;
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
  /** Level 2: where playback is now (seconds on the title's timeline), when the adapter knows. */
  currentPosition?(): number;
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
  /** `position`: for a menu key a native player forwards during playback, where it was pressed (seconds). */
  attach(onKey: (key: Key, position?: number) => boolean): void;
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
  // Level 2: the remote's Menu and Title keys (spec 6.6); M and T stand in for them on a keyboard.
  ContextMenu: "menu",
  m: "menu",
  M: "menu",
  t: "title",
  T: "title",
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
  /** What plays: one piece for a single file, several for a title made of segments (spec 4.2). */
  private pieces: { url?: string; start: number; end?: number; offset: number }[] = [];
  private piece = 0;
  private label = "";

  constructor(container: HTMLElement) {
    this.video = document.createElement("video");
    this.video.className = "ms-video";
    this.video.controls = true;
    this.video.playsInline = true;
    this.video.hidden = true;
    container.appendChild(this.video);
    this.video.addEventListener("ended", () => this.nextPiece());
    this.video.addEventListener("timeupdate", () => {
      const p = this.pieces[this.piece];
      // A segment that ends inside its file hands over to the next one there.
      if (p?.end !== undefined && this.video.currentTime >= p.end) return this.nextPiece();
      this.clock?.tick(this.position());
    });
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

  play({ url, start, label, audio, end, cues, segments }: PlayRequest) {
    this.pendingAudio = audio;
    this.clock = undefined;
    this.label = label;
    // Each piece's place on the title's timeline.
    let offset = 0;
    const parts: { url?: string; start: number; end?: number }[] = segments ?? [{ url, start: 0 }];
    this.pieces = parts.map((seg) => {
      const piece = { url: seg.url, start: seg.start, end: seg.end, offset };
      offset += (seg.end ?? Infinity) - seg.start;
      return piece;
    });
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
    // The piece holding `start` (the last one runs to the end of its file).
    let i = 0;
    while (i < this.pieces.length - 1 && start >= this.pieces[i + 1]!.offset) i++;
    this.load(i, start - this.pieces[i]!.offset);
  }

  /** Where playback is on the title's timeline. */
  currentPosition(): number {
    return this.position();
  }

  private position(): number {
    const p = this.pieces[this.piece];
    return p ? p.offset + Math.max(0, (this.video.currentTime || 0) - p.start) : 0;
  }

  /** Play piece i from `into` seconds after its start. */
  private load(i: number, into: number) {
    const v = this.video;
    const p = this.pieces[i]!;
    this.piece = i;
    if (!p.url) {
      queueMicrotask(() => this.onError?.(`Can't find "${this.label}".`));
      return;
    }
    v.hidden = false;
    v.title = this.label;
    v.src = p.url;
    const seek = () => {
      const at = p.start + into;
      if (at > 0) v.currentTime = at;
      this.applyAudioTrack();
      void v.play().catch(() => {
        /* autoplay may need a gesture; controls are visible */
      });
    };
    if (v.readyState >= HTMLMediaElement.HAVE_METADATA) seek();
    else v.addEventListener("loadedmetadata", seek, { once: true });
    v.focus({ preventScroll: true });
  }

  /** The piece playing ran out: the next one, or the title has ended. */
  private nextPiece() {
    if (this.video.hidden) return;
    if (this.piece + 1 < this.pieces.length) return this.load(this.piece + 1, 0);
    this.hide();
    this.onEnded?.();
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
    const position = this.position();
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
