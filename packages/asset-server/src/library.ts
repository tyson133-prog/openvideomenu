import type { Catalog } from "./catalog.js";

/**
 * The media server's library folders, so the server follows the libraries instead of a folder list.
 * This needs a token of the server's own (PLEX_TOKEN, JELLYFIN_API_KEY), kept in its settings.
 */
export interface LibrarySource {
  readonly name: string;
  /** The library folders as the media server sees them. Throws when it can't be reached. */
  folders(): Promise<string[]>;
}

type Fetch = typeof fetch;

/** Plex: movie and TV sections (other kinds have no menus). GET /library/sections. */
export class PlexLibrary implements LibrarySource {
  readonly name = "Plex";
  constructor(
    private readonly url: string,
    private readonly token: string,
    private readonly fetchImpl: Fetch = fetch,
  ) {}

  async folders(): Promise<string[]> {
    const res = await this.fetchImpl(new URL("/library/sections", this.url), {
      headers: { Accept: "application/json", "X-Plex-Token": this.token },
      signal: AbortSignal.timeout(10_000),
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const body = (await res.json()) as { MediaContainer?: { Directory?: { type?: string; Location?: { path?: string }[] }[] } };
    return (body.MediaContainer?.Directory ?? [])
      .filter((d) => d.type === "movie" || d.type === "show")
      .flatMap((d) => (d.Location ?? []).map((l) => l.path).filter((p): p is string => typeof p === "string" && p !== ""));
  }
}

/** Jellyfin: movie, TV, mixed and home-video libraries. GET /Library/VirtualFolders. */
export class JellyfinLibrary implements LibrarySource {
  readonly name = "Jellyfin";
  private static readonly KINDS = new Set(["movies", "tvshows", "mixed", "homevideos"]);
  constructor(
    private readonly url: string,
    private readonly apiKey: string,
    private readonly fetchImpl: Fetch = fetch,
  ) {}

  async folders(): Promise<string[]> {
    const res = await this.fetchImpl(new URL("/Library/VirtualFolders", this.url), {
      headers: { Accept: "application/json", Authorization: `MediaBrowser Token="${this.apiKey}"` },
      signal: AbortSignal.timeout(10_000),
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const body = (await res.json()) as { CollectionType?: string | null; Locations?: string[] }[];
    return body
      .filter((f) => f.CollectionType == null || JellyfinLibrary.KINDS.has(f.CollectionType))
      .flatMap((f) => (f.Locations ?? []).filter((p) => typeof p === "string" && p !== ""));
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
 * plus any extra folders. Re-reads them every [intervalMs]; when a server can't be reached, its last
 * good list stands.
 */
export class LibraryFollower {
  private readonly lastGood = new Map<string, string[]>();
  private readonly failing = new Set<string>();
  private timer?: NodeJS.Timeout;

  constructor(
    private readonly sources: LibrarySource[],
    private readonly extra: string[],
    private readonly pathMap: PathMapping[],
    private readonly catalog: Catalog,
    private readonly log: (line: string) => void = console.log,
    private readonly intervalMs = 5 * 60_000,
  ) {}

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

  /** Read every source's folders; when the list changed, give the catalog the new folders and rescan. */
  async refresh(): Promise<void> {
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
