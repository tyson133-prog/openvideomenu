/**
 * Library changes as they happen, from the media server's own notification socket: Plex's
 * /:/websockets/notifications, Jellyfin's /socket. On a change (after a short quiet spell, since a
 * library scan sends many) the server refreshes its library items and rescans its menus, so a new
 * film's menu shows up as soon as the media server has the film. Needs the server's own token.
 */

/** The little of the WebSocket API used here (Node 22's global WebSocket has it). */
export interface SocketLike {
  onopen: ((ev: unknown) => void) | null;
  onmessage: ((ev: { data: unknown }) => void) | null;
  onclose: ((ev: unknown) => void) | null;
  onerror: ((ev: unknown) => void) | null;
  send(data: string): void;
  close(): void;
}

export type MakeSocket = (url: string) => SocketLike;

export interface LiveSource {
  readonly name: string;
  /** The socket's URL (it carries the token: never log it). */
  readonly url: string;
  /** True when this message says the library changed. */
  isChange(message: unknown): boolean;
  /** Called with each message, for keep-alives the server asks for. */
  onMessage?(message: unknown, send: (data: string) => void): void;
  stop?(): void;
}

const wsUrl = (http: string, path: string) => {
  const u = new URL(path, http);
  u.protocol = u.protocol === "https:" ? "wss:" : "ws:";
  return u;
};

/** Plex: timeline entries (items added, changed, removed) and library activities that ended (a scan). */
export class PlexLive implements LiveSource {
  readonly name = "Plex";
  readonly url: string;
  constructor(plexUrl: string, token: string) {
    const u = wsUrl(plexUrl, "/:/websockets/notifications");
    u.searchParams.set("X-Plex-Token", token);
    this.url = u.toString();
  }
  isChange(message: unknown): boolean {
    const n = (message as { NotificationContainer?: { type?: string; TimelineEntry?: unknown[]; ActivityNotification?: { event?: string; Activity?: { type?: string } }[] } })
      .NotificationContainer;
    if (!n) return false;
    if (n.type === "timeline") return (n.TimelineEntry?.length ?? 0) > 0;
    if (n.type === "activity") return (n.ActivityNotification ?? []).some((a) => a.event === "ended" && (a.Activity?.type ?? "").startsWith("library."));
    return false;
  }
}

/** Jellyfin: LibraryChanged messages. It asks clients to send KeepAlive within its ForceKeepAlive time. */
export class JellyfinLive implements LiveSource {
  readonly name = "Jellyfin";
  readonly url: string;
  private keepAlive?: NodeJS.Timeout;
  constructor(jellyfinUrl: string, apiKey: string) {
    const u = wsUrl(jellyfinUrl, "/socket");
    u.searchParams.set("api_key", apiKey);
    u.searchParams.set("deviceId", "openvideomenu-menu-server");
    this.url = u.toString();
  }
  isChange(message: unknown): boolean {
    return (message as { MessageType?: string }).MessageType === "LibraryChanged";
  }
  onMessage(message: unknown, send: (data: string) => void) {
    const m = message as { MessageType?: string; Data?: unknown };
    if (m.MessageType !== "ForceKeepAlive") return;
    const seconds = typeof m.Data === "number" && m.Data > 0 ? m.Data : 60;
    clearInterval(this.keepAlive);
    this.keepAlive = setInterval(() => send(JSON.stringify({ MessageType: "KeepAlive" })), (seconds * 1000) / 2);
    this.keepAlive.unref?.();
    send(JSON.stringify({ MessageType: "KeepAlive" }));
  }
  stop() {
    clearInterval(this.keepAlive);
  }
}

export interface LiveOptions {
  /** A library changed (debounced). */
  onChange: () => void;
  /** Whether every source is connected now (the server rescans less often while they are). */
  onConnected?: (connected: boolean) => void;
  log?: (line: string) => void;
  makeSocket?: MakeSocket;
  /** Quiet time after the last change before [onChange] runs. */
  settleMs?: number;
}

/** Listens to each source's socket, reconnecting with backoff (5 s doubling to 2 min). */
export class LiveUpdates {
  private readonly connected = new Set<string>();
  private readonly sockets = new Map<string, SocketLike>();
  private readonly retryTimers = new Set<NodeJS.Timeout>();
  private settle?: NodeJS.Timeout;
  private stopped = false;

  constructor(
    private readonly sources: LiveSource[],
    private readonly o: LiveOptions,
  ) {}

  /** False when this Node has no WebSocket (then the server keeps scanning every 30 s). */
  start(): boolean {
    const make = this.o.makeSocket ?? (typeof WebSocket === "function" ? (url: string) => new WebSocket(url) as unknown as SocketLike : undefined);
    if (!make) {
      this.o.log?.("This Node.js has no WebSocket: library changes are found by the regular scan.");
      return false;
    }
    for (const s of this.sources) this.connect(s, make, 5_000);
    return true;
  }

  stop() {
    this.stopped = true;
    clearTimeout(this.settle);
    for (const t of this.retryTimers) clearTimeout(t);
    for (const s of this.sources) s.stop?.();
    for (const socket of this.sockets.values()) socket.close();
  }

  private connect(source: LiveSource, make: MakeSocket, backoff: number) {
    if (this.stopped) return;
    let socket: SocketLike;
    try {
      socket = make(source.url);
    } catch (e) {
      return this.retry(source, make, backoff, (e as Error).message);
    }
    this.sockets.set(source.name, socket);
    let opened = false;
    socket.onopen = () => {
      opened = true;
      this.connected.add(source.name);
      this.o.log?.(`Listening for library changes from ${source.name}.`);
      this.report();
    };
    socket.onmessage = (ev) => {
      let message: unknown;
      try {
        message = JSON.parse(typeof ev.data === "string" ? ev.data : String(ev.data));
      } catch {
        return;
      }
      source.onMessage?.(message, (d) => socket.send(d));
      if (source.isChange(message)) this.changed();
    };
    socket.onerror = () => {};
    socket.onclose = () => {
      this.sockets.delete(source.name);
      if (this.connected.delete(source.name)) this.report();
      source.stop?.();
      // A connection that worked starts the backoff over; one that never opened backs off further.
      this.retry(source, make, opened ? 5_000 : backoff, opened ? "connection closed" : "couldn't connect");
    };
  }

  private retry(source: LiveSource, make: MakeSocket, backoff: number, why: string) {
    if (this.stopped) return;
    if (backoff === 5_000) this.o.log?.(`Library changes from ${source.name}: ${why}; trying again.`);
    const t = setTimeout(() => {
      this.retryTimers.delete(t);
      this.connect(source, make, Math.min(backoff * 2, 120_000));
    }, backoff);
    t.unref?.();
    this.retryTimers.add(t);
  }

  private report() {
    this.o.onConnected?.(this.sources.every((s) => this.connected.has(s.name)));
  }

  private changed() {
    clearTimeout(this.settle);
    this.settle = setTimeout(() => this.o.onChange(), this.o.settleMs ?? 5_000);
    this.settle.unref?.();
  }
}
