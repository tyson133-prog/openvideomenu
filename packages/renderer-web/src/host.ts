/**
 * Running the renderer inside a native app (Android TV web view, and later others).
 *
 * The native side exposes a message port object (Android: WebViewCompat.addWebMessageListener named
 * "openVideoMenuHost") with postMessage(string) and an onmessage handler. Everything is JSON strings.
 *
 * page → native
 *   {type:"ready"}                                       page loaded, send "init"
 *   {type:"play", mediaId, ref?, url?, start, label, audio?, subtitle?, angle?, videoTrack?, end?, cues?, segments?}
 *                                                        segments (Level 2): [{mediaId, ref, url?, start, end?,
 *                                                        angle?, videoTrack?}] played as one title; start, end,
 *                                                        cues and stop positions are on its timeline
 *                                                        play this in the native player (audio: 1-based
 *                                                        track; subtitle: 1-based, 0 = off; angle: which
 *                                                        angle file, ref/url already that file's;
 *                                                        videoTrack: 1-based, for angles as video tracks)
 *   {type:"stop"}                                        stop playback, without reporting it (a cue opened a menu)
 *   {type:"endHold"}                                     Level 2: the viewer pressed an overlay button over a held
 *                                                        frame: end the hold (as Enter does)
 *   {type:"seek", position}                              Level 2: move the playback in progress there (seconds
 *                                                        on the title's timeline); cues as for a viewer's seek
 *   {type:"overlay", showing}                            an overlay's buttons show over the video (Level 2):
 *                                                        show the page over it and send it the arrows and Enter
 *   {type:"exit"}                                        Back pressed at the top menu
 *   {type:"error", message}                              manifest couldn't be loaded or is invalid
 * native → page
 *   {type:"init", manifestUrl, lastStop?, view?, showHidden?}
 *   {type:"ended"}                                       the item played to the end
 *   {type:"stopped", position}                           the viewer stopped early (seconds)
 *   {type:"playError", message}                          the item couldn't be played
 *   {type:"key", key, position?}                         a remote key the page didn't receive itself (e.g. Back);
 *                                                        Level 2: Menu/Title during playback, with the position
 *   {type:"cue", id} / {type:"cueEnd", id}               playback passed a cue the play message listed (Level 2)
 */
import type { Key } from "@openvideomenu/core";
import { KeyboardInput, type InputAdapter, type PlaybackAdapter, type PlayRequest } from "./adapters.js";
import { loadPlayer } from "./load.js";
import type { MenuPlayer } from "./player.js";

export interface HostPort {
  postMessage(message: string): void;
  onmessage: ((event: { data: unknown }) => void) | null;
}

type HostMessage =
  | { type: "init"; manifestUrl: string; lastStop?: { media: string; position: number }; view?: "menu" | "fallback"; showHidden?: boolean }
  | { type: "ended" }
  | { type: "stopped"; position: number }
  | { type: "playError"; message: string }
  | { type: "key"; key: Key; position?: number }
  | { type: "cue"; id: string }
  | { type: "cueEnd"; id: string };

/** Playback handed to the native player; results come back as host messages. */
export class BridgePlayback implements PlaybackAdapter {
  onEnded?: () => void;
  onStopped?: (position: number) => void;
  onError?: (message: string) => void;

  constructor(private readonly send: (message: object) => void) {}

  onCue?: (id: string) => void;
  onCueEnd?: (id: string) => void;

  play({ mediaId, ref, url, start, label, audio, subtitle, angle, videoTrack, end, cues, segments, holds }: PlayRequest) {
    this.send({ type: "play", mediaId, ref, url, start, label, audio, subtitle, angle, videoTrack, end, cues, segments, holds });
  }

  /** Level 2: an overlay button was pressed over a held frame; the native player goes on. */
  endHold(): boolean {
    this.send({ type: "endHold" });
    return true;
  }

  /** Level 2: a seek action moved the title; the native player moves there. */
  seek(position: number) {
    this.send({ type: "seek", position });
  }

  /** The navigator left playback by itself (a cue opened a menu); the native player owns the position. */
  stop(): number {
    this.send({ type: "stop" });
    return 0;
  }

  destroy() {}
}

/** Keyboard/remote keys delivered to the page, plus keys the native side forwards. */
export class HostInput implements InputAdapter {
  private readonly keyboard = new KeyboardInput();
  private onKey?: (key: Key, position?: number) => boolean;

  attach(onKey: (key: Key, position?: number) => boolean) {
    this.onKey = onKey;
    this.keyboard.attach(onKey);
  }

  detach() {
    this.keyboard.detach();
    this.onKey = undefined;
  }

  deliver(key: Key, position?: number) {
    this.onKey?.(key, position);
  }
}

/** Start the renderer under a native host. Returns once the host has been told the page is ready. */
export function runInHost(port: HostPort, container: HTMLElement = document.body): void {
  const send = (message: object) => port.postMessage(JSON.stringify(message));
  const playback = new BridgePlayback(send);
  const input = new HostInput();
  let player: MenuPlayer | undefined;

  port.onmessage = (event) => {
    let msg: HostMessage;
    try {
      msg = JSON.parse(String(event.data)) as HostMessage;
    } catch {
      return;
    }
    switch (msg.type) {
      case "init":
        player?.destroy();
        void loadPlayer(container, msg.manifestUrl, {
          playback: () => playback,
          input,
          view: msg.view,
          showHidden: msg.showHidden,
          initialLastStop: msg.lastStop,
          onExit: () => send({ type: "exit" }),
          onOverlay: (showing) => send({ type: "overlay", showing }),
        }).then((p) => {
          player = p;
          if (!p) send({ type: "error", message: `Couldn't load ${msg.manifestUrl}` });
        });
        break;
      case "ended":
        playback.onEnded?.();
        break;
      case "stopped":
        playback.onStopped?.(msg.position);
        break;
      case "cue":
        playback.onCue?.(msg.id);
        break;
      case "cueEnd":
        playback.onCueEnd?.(msg.id);
        break;
      case "playError":
        playback.onError?.(msg.message);
        break;
      case "key":
        input.deliver(msg.key, msg.position);
        break;
    }
  };
  send({ type: "ready" });
}
