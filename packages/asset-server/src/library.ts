import type { Catalog } from "./catalog.js";

/**
 * The media server's library folders, so the server follows the libraries instead of a folder list.
 * This needs a token of the server's own (PLEX_TOKEN, JELLYFIN_API_KEY), kept in its settings.
 */
export interface LibrarySource {
  readonly name: string;
  /** "plex" or "jellyfin": what the ids in [items] are ids of. */
  readonly kind: "plex" | "jellyfin";
  /** The library folders as the media server sees them. Throws when it can't be reached. */
  folders(): Promise<string[]>;
  /** The media server's own id (Plex machineIdentifier, Jellyfin server Id), which apps compare with theirs. */
  serverId(): Promise<string>;
  /** Every film and episode with its files, as the media server sees them. Throws when it can't be reached. */
  items(): Promise<LibraryItem[]>;
}

/** A film or an episode in the media server's library, and the files it plays. */
export interface LibraryItem {
  id: string;
  type: "movie" | "episode";
  /** For an episode: its show's id. */
  show?: string;
  files: string[];
}

type Fetch = typeof fetch;

/** Plex: movie and TV sections (other kinds have no menus). GET /library/sections. */
export class PlexLibrary implements LibrarySource {
  readonly name = "Plex";
  readonly kind = "plex";
  private machineId?: string;
  constructor(
    private readonly url: string,
    private readonly token: string,
    private readonly fetchImpl: Fetch = fetch,
  ) {}

  private async get<T>(path: string, timeoutMs = 10_000): Promise<T> {
    const res = await this.fetchImpl(new URL(path, this.url), {
      headers: { Accept: "application/json", "X-Plex-Token": this.token },
      signal: AbortSignal.timeout(timeoutMs),
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    return (await res.json()) as T;
  }

  private async sections() {
    type Sections = { MediaContainer?: { Directory?: { key?: string; type?: string; Location?: { path?: string }[] }[] } };
    return ((await this.get<Sections>("/library/sections")).MediaContainer?.Directory ?? []).filter((d) => d.type === "movie" || d.type === "show");
  }

  async folders(): Promise<string[]> {
    return (await this.sections()).flatMap((d) => (d.Location ?? []).map((l) => l.path).filter((p): p is string => typeof p === "string" && p !== ""));
  }

  async serverId(): Promise<string> {
    this.machineId ??= (await this.get<{ MediaContainer?: { machineIdentifier?: string } }>("/identity")).MediaContainer?.machineIdentifier;
    if (!this.machineId) throw new Error("Plex didn't report its machineIdentifier");
    return this.machineId;
  }

  /** Films of movie sections (type 1) and episodes of show sections (type 4), with their parts' files. */
  async items(): Promise<LibraryItem[]> {
    type Part = { file?: string };
    type Meta = { ratingKey?: string; grandparentRatingKey?: string; Media?: { Part?: Part[] }[] };
    const out: LibraryItem[] = [];
    for (const section of await this.sections()) {
      if (!section.key) continue;
      const type = section.type === "movie" ? 1 : 4;
      const body = await this.get<{ MediaContainer?: { Metadata?: Meta[] } }>(`/library/sections/${encodeURIComponent(section.key)}/all?type=${type}`, 60_000);
      for (const m of body.MediaContainer?.Metadata ?? []) {
        const files = (m.Media ?? []).flatMap((media) => (media.Part ?? []).map((p) => p.file)).filter((f): f is string => typeof f === "string" && f !== "");
        if (!m.ratingKey || files.length === 0) continue;
        out.push(type === 1 ? { id: m.ratingKey, type: "movie", files } : { id: m.ratingKey, type: "episode", show: m.grandparentRatingKey, files });
      }
    }
    return out;
  }
}

/** Jellyfin: movie, TV, mixed and home-video libraries. GET /Library/VirtualFolders. */
export class JellyfinLibrary implements LibrarySource {
  readonly name = "Jellyfin";
  readonly kind = "jellyfin";
  private static readonly KINDS = new Set(["movies", "tvshows", "mixed", "homevideos"]);
  private id?: string;
  constructor(
    private readonly url: string,
    private readonly apiKey: string,
    private readonly fetchImpl: Fetch = fetch,
  ) {}

  private async get<T>(path: string, timeoutMs = 10_000): Promise<T> {
    const res = await this.fetchImpl(new URL(path, this.url), {
      headers: { Accept: "application/json", Authorization: `MediaBrowser Token="${this.apiKey}"` },
      signal: AbortSignal.timeout(timeoutMs),
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    return (await res.json()) as T;
  }

  async folders(): Promise<string[]> {
    const body = await this.get<{ CollectionType?: string | null; Locations?: string[] }[]>("/Library/VirtualFolders");
    return body
      .filter((f) => f.CollectionType == null || JellyfinLibrary.KINDS.has(f.CollectionType))
      .flatMap((f) => (f.Locations ?? []).filter((p) => typeof p === "string" && p !== ""));
  }

  async serverId(): Promise<string> {
    this.id ??= (await this.get<{ Id?: string }>("/System/Info/Public")).Id;
    if (!this.id) throw new Error("Jellyfin didn't report its server Id");
    return this.id;
  }

  /** Every film and episode, with its files (each media source's path, else the item's). */
  async items(): Promise<LibraryItem[]> {
    type Item = { Id?: string; Type?: string; SeriesId?: string; Path?: string; MediaSources?: { Path?: string }[] };
    const body = await this.get<{ Items?: Item[] }>("/Items?Recursive=true&IncludeItemTypes=Movie,Episode&Fields=Path,MediaSources", 60_000);
    const out: LibraryItem[] = [];
    for (const it of body.Items ?? []) {
      const files = [...new Set([...(it.MediaSources ?? []).map((s) => s.Path), it.Path])].filter((f): f is string => typeof f === "string" && f !== "");
      if (!it.Id || files.length === 0) continue;
      if (it.Type === "Movie") out.push({ id: it.Id, type: "movie", files });
      else if (it.Type === "Episode") out.push({ id: it.Id, type: "episode", show: it.SeriesId, files });
    }
    return out;
  }
}

// --- matching files to library items ---------------------------------------------------------

/** What the index says about a media: the library item that plays it, on which media server. */
export interface IndexItem {
  /** The media server's id (Plex machineIdentifier, Jellyfin server Id). */
  server: string;
  id: string;
  type: "movie" | "episode";
  show?: string;
}

const segmentsOf = (path: string) => path.split(/[\\/]/).filter((s) => s !== "").map((s) => s.toLowerCase());

/**
 * The media servers' files, for finding the item that plays a file. The two machines see the same
 * folders under different roots ("K:\MP4\Movies\X (1999)\X.mp4" against "//nas/MP4/Movies/X (1999)/X.mp4"),
 * so files match by their trailing path segments, case-insensitively, as the TV apps match them: at
 * least the file name and its folder, so two films' "Trailer.mp4" never match each other. The longest
 * match wins; a tie between two items is no match.
 */
export class ItemIndex {
  private readonly byTail = new Map<string, { segments: string[]; item: IndexItem }[]>();

  add(server: string, items: LibraryItem[]) {
    for (const it of items) {
      const item: IndexItem = { server, id: it.id, type: it.type, ...(it.show ? { show: it.show } : {}) };
      for (const file of it.files) {
        const segments = segmentsOf(file);
        if (segments.length < 2) continue;
        const key = segments.slice(-2).join("/");
        const list = this.byTail.get(key) ?? [];
        list.push({ segments, item });
        this.byTail.set(key, list);
      }
    }
  }

  /** The items (at most one per media server) that play `file`. */
  find(file: string): IndexItem[] {
    const segments = segmentsOf(file);
    if (segments.length < 2) return [];
    const best = new Map<string, { score: number; item?: IndexItem }>();
    for (const c of this.byTail.get(segments.slice(-2).join("/")) ?? []) {
      let n = 0;
      while (n < c.segments.length && n < segments.length && c.segments[c.segments.length - 1 - n] === segments[segments.length - 1 - n]) n++;
      const b = best.get(c.item.server);
      if (!b || n > b.score) best.set(c.item.server, { score: n, item: c.item });
      else if (n === b.score && b.item && b.item.id !== c.item.id) best.set(c.item.server, { score: n, item: undefined });
    }
    return [...best.values()].flatMap((b) => (b.item ? [b.item] : []));
  }
}

// --- path maps -------------------------------------------------------------------------------

export interface PathMapping {
  from: string;
  to: string;
}

/** "from=to" pairs (from --path-map, or PATH_MAP split on ";"). */
export function parsePathMap(pairs: string[]): PathMapping[] {
  return pairs.map((pair) => {
    const at = pair.indexOf("=");
    if (at <= 0 || at === pair.length - 1) throw new Error(`--path-map needs <from>=<to>, e.g. /data=/mnt/media; got "${pair}".`);
    return { from: pair.slice(0, at).trim(), to: pair.slice(at + 1).trim() };
  });
}

/** A Windows path: a drive letter or a UNC share. Their comparisons ignore case and separator style. */
const isWindowsPath = (p: string) => /^[a-zA-Z]:([\\/]|$)/.test(p) || /^(\\\\|\/\/)/.test(p);

/**
 * A media server's path as a path on this machine: the longest matching `from` prefix (at a folder
 * boundary) is replaced by its `to`. Unmatched paths come back unchanged.
 */
export function applyPathMap(path: string, map: PathMapping[]): string {
  let best: PathMapping | undefined;
  for (const m of map) {
    if (prefixLength(path, m.from) !== undefined && (!best || m.from.length > best.from.length)) best = m;
  }
  if (!best) return path;
  const rest = path.slice(prefixLength(path, best.from)!).replace(/^[\\/]+/, "");
  const sep = isWindowsPath(best.to) || best.to.includes("\\") ? "\\" : "/";
  const to = sep === "\\" ? best.to.replace(/\//g, "\\") : best.to;
  if (rest === "") return to;
  return to.replace(/[\\/]+$/, "") + sep + rest.split(/[\\/]/).join(sep);
}

/** How much of `path` the prefix `from` covers, or undefined when it isn't a prefix at a folder boundary. */
function prefixLength(path: string, from: string): number | undefined {
  const windows = isWindowsPath(from) || isWindowsPath(path);
  const norm = (s: string) => {
    const unified = s.replace(/\\/g, "/");
    return windows ? unified.toLowerCase() : unified;
  };
  const p = norm(path);
  const f = norm(from).replace(/\/+$/, "");
  if (f === "") return undefined;
  if (p === f) return path.length;
  return p.startsWith(f + "/") ? f.length : undefined;
}

// --- settings file -----------------------------------------------------------------------------

/** KEY=VALUE lines, as in an env file: # comments and blank lines skipped, values optionally quoted. */
export function parseEnvFile(text: string): Record<string, string> {
  const out: Record<string, string> = {};
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (line === "" || line.startsWith("#")) continue;
    const at = line.indexOf("=");
    if (at <= 0) continue;
    const key = line.slice(0, at).trim().replace(/^export\s+/, "");
    let value = line.slice(at + 1).trim();
    if (value.length >= 2 && (value[0] === '"' || value[0] === "'") && value.at(-1) === value[0]) value = value.slice(1, -1);
    out[key] = value;
  }
  return out;
}

// --- following the libraries -------------------------------------------------------------------

/**
 * Keeps the catalog's media folders in step with the media servers' libraries (mapped to this machine)
 * plus any extra folders, and knows which library item plays each file, for the index. Re-reads the
 * folders every [intervalMs] and the items every [itemsMs] (and both whenever [refresh] is asked for
 * items, e.g. on a library change); when a server can't be reached, its last good lists stand.
 */
export class LibraryFollower {
  private readonly lastGood = new Map<string, string[]>();
  private readonly failing = new Set<string>();
  private readonly lastItems = new Map<string, { server: string; items: LibraryItem[] }>();
  private readonly failingItems = new Set<string>();
  private items = new ItemIndex();
  private itemsAt = 0;
  private timer?: NodeJS.Timeout;

  constructor(
    private readonly sources: LibrarySource[],
    private readonly extra: string[],
    private readonly pathMap: PathMapping[],
    private readonly catalog: Catalog,
    private readonly log: (line: string) => void = console.log,
    private readonly intervalMs = 5 * 60_000,
    private readonly itemsMs = 30 * 60_000,
    private readonly now: () => number = Date.now,
  ) {
    catalog.itemsFor = (file) => this.items.find(file);
  }

  /** The folders as of the latest refresh: each source's (mapped), then the extra ones, without repeats. */
  roots(): string[] {
    const all = [...[...this.lastGood.values()].flat().map((p) => applyPathMap(p, this.pathMap)), ...this.extra];
    const seen = new Set<string>();
    return all.filter((p) => {
      const key = isWindowsPath(p) ? p.replace(/\\/g, "/").toLowerCase() : p;
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    });
  }

  /**
   * Read every source's folders; when the list changed, give the catalog the new folders and rescan.
   * The library items too when [withItems] (or when they're [itemsMs] old).
   */
  async refresh(withItems = false): Promise<void> {
    if (withItems || this.now() - this.itemsAt >= this.itemsMs) await this.refreshItems();
    await Promise.all(
      this.sources.map(async (s) => {
        try {
          this.lastGood.set(s.name, await s.folders());
          if (this.failing.delete(s.name)) this.log(`Reading library folders from ${s.name} again.`);
        } catch (e) {
          if (!this.failing.has(s.name)) {
            this.failing.add(s.name);
            const keeping = this.lastGood.has(s.name) ? "keeping the last list" : "will try again";
            this.log(`WARNING: couldn't read library folders from ${s.name} (${(e as Error).message}); ${keeping}.`);
          }
        }
      }),
    );
    if (this.catalog.setRoots(this.roots())) {
      this.log(`Library folders: ${this.roots().join(", ") || "(none)"}`);
      void this.catalog.scan();
    }
  }

  /** Which library item plays each file: read from every source; a source that fails keeps its last list. */
  private async refreshItems(): Promise<void> {
    this.itemsAt = this.now();
    await Promise.all(
      this.sources.map(async (s) => {
        try {
          this.lastItems.set(s.name, { server: await s.serverId(), items: await s.items() });
          this.failingItems.delete(s.name);
        } catch (e) {
          if (!this.failingItems.has(s.name)) {
            this.failingItems.add(s.name);
            this.log(`WARNING: couldn't read library items from ${s.name} (${(e as Error).message}); apps match menus themselves meanwhile.`);
          }
        }
      }),
    );
    const next = new ItemIndex();
    for (const { server, items } of this.lastItems.values()) next.add(server, items);
    this.items = next;
    this.catalog.invalidateIndex();
  }

  /** Refresh now and every [intervalMs]; until a first list arrives, every 30 s. */
  async start(): Promise<void> {
    await this.refresh();
    const schedule = () => {
      const waiting = this.sources.some((s) => !this.lastGood.has(s.name));
      this.timer = setTimeout(async () => {
        await this.refresh();
        schedule();
      }, waiting ? Math.min(30_000, this.intervalMs) : this.intervalMs);
      this.timer.unref?.();
    };
    schedule();
  }

  stop() {
    clearTimeout(this.timer);
    this.timer = undefined;
  }
}
