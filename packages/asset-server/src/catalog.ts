import { createHash } from "node:crypto";
import { readFile, stat } from "node:fs/promises";
import { gzipSync } from "node:zlib";
import { isAbsolute, join, posix, relative, resolve } from "node:path";
import { FORMAT_VERSION, menuAssets, parseManifest, type MediaRef } from "@openvideomenu/core";
import { scanManifests } from "./files.js";
import { checkAssetFiles, checkIgnoreFiles, eachLimited } from "./media-check.js";

export interface CatalogMedia {
  id: string;
  kind?: string;
  label?: string;
  ref: MediaRef;
  /**
   * For relative-path refs: the file's path relative to the media root, forward slashes. TV apps
   * match its trailing segments against the file paths their media server reports.
   */
  path?: string;
}

export interface CatalogEntry {
  /** Stable id for this manifest (hash of root + path). */
  id: string;
  root: number;
  /** Manifest path relative to its media root, forward slashes. */
  path: string;
  /** URL path (under /v1/files/) the manifest is served at; relative asset paths resolve from it. */
  url: string;
  valid: boolean;
  errors?: string[];
  /** Problems that don't stop the menu working everywhere: missing menu art, MP4s without faststart. */
  warnings?: string[];
  version?: string;
  title?: { name: string; year?: number; ids?: Record<string, string | undefined> };
  source?: { kind: string; generator?: string };
  hasMenus: boolean;
  media: CatalogMedia[];
  /** Menu assets, relative to the media root. Kept on the server (it's the allowlist); not in the index. */
  assets: string[];
  modified: string;
}

/** What TV apps get per manifest: enough to match titles and open menus, without the asset list. */
export type IndexEntry = Omit<CatalogEntry, "assets" | "warnings">;

/** The index as sent: JSON, a gzipped copy, and an ETag, made once per catalog change. */
export interface IndexBody {
  json: string;
  gzip: Buffer;
  etag: string;
}

interface Cached {
  mtimeMs: number;
  entry: CatalogEntry;
  /** Asset checks (files present, MP4s faststart) done for this version of the manifest. */
  checked?: boolean;
}

/** Encode each path segment for a URL, keeping the slashes. */
export const encodePath = (path: string) => path.split("/").map(encodeURIComponent).join("/");

/**
 * The manifests under the media roots, and the exact set of files that may be served: the manifests
 * themselves plus the menu assets they name. Nothing else — in particular no media — is reachable.
 */
export class Catalog {
  private cache = new Map<string, Cached>();
  private list: CatalogEntry[] = [];
  private allowed = new Set<string>();
  private body?: IndexBody;
  private scanning?: Promise<void>;
  private firstScan?: Promise<void>;
  private timer?: NodeJS.Timeout;

  /** Called after each scan with manifests whose asset checks found problems (first time seen or changed). */
  onWarnings?: (entries: CatalogEntry[]) => void;

  constructor(
    readonly roots: string[],
    private readonly rescanMs = 5 * 60_000,
  ) {
    this.roots = roots.map((r) => resolve(r));
  }

  /**
   * Scan now and every [rescanMs] in the background. Requests never wait for a rescan; they only
   * wait for the first scan (see [ready]).
   */
  start(): Promise<void> {
    this.timer ??= setInterval(() => void this.scan(), this.rescanMs);
    this.timer.unref?.();
    return this.ready();
  }

  stop() {
    clearInterval(this.timer);
    this.timer = undefined;
  }

  /** Resolves once the catalog has been scanned at least once. */
  ready(): Promise<void> {
    this.firstScan ??= this.scan().then(() => void (this.isReady = true));
    return this.firstScan;
  }

  /** True once the first scan has finished. */
  isReady = false;

  /** The manifests from the latest finished scan. */
  entries(): CatalogEntry[] {
    return this.list;
  }

  /** The index TV apps download: serialized, gzipped and tagged once per change, not per request. */
  index(meta: Record<string, unknown>): IndexBody {
    if (!this.body) {
      const manifests: IndexEntry[] = this.list.map(({ assets: _a, warnings: _w, ...rest }) => rest);
      const json = JSON.stringify({ ...meta, manifests });
      this.body = { json, gzip: gzipSync(json), etag: `"${createHash("sha1").update(json).digest("hex").slice(0, 20)}"` };
    }
    return this.body;
  }

  /** Absolute path of an allowed file, or undefined. `path` is root-relative with forward slashes. */
  resolveFile(root: number, path: string): string | undefined {
    const normalized = posix.normalize(path);
    if (normalized.startsWith("../") || normalized === ".." || posix.isAbsolute(normalized)) return undefined;
    if (!this.allowed.has(`${root}:${normalized}`)) return undefined;
    const base = this.roots[root];
    if (base === undefined) return undefined;
    const full = resolve(base, ...normalized.split("/"));
    // Containment via relative(): a plain prefix check breaks on roots that resolve with a trailing
    // separator, such as UNC shares ("\\server\share\") and drive roots ("D:\").
    const rel = relative(base, full);
    return rel !== "" && !rel.startsWith("..") && !isAbsolute(rel) ? full : undefined;
  }

  /**
   * Find manifests and read the new or changed ones; unchanged ones (same modified time) are reused.
   * The list is published as soon as manifests are read. Asset checks run afterwards and only for
   * new or changed manifests, so they never hold up the index. Concurrent calls share one scan.
   */
  scan(): Promise<void> {
    this.scanning ??= this.doScan().finally(() => (this.scanning = undefined));
    return this.scanning;
  }

  private async doScan() {
    const found: { key: string; root: number; path: string; file: string }[] = [];
    await Promise.all(
      this.roots.map(async (root, rootIndex) => {
        let paths: string[];
        try {
          paths = await scanManifests(root);
        } catch {
          return; // root missing or unreadable (e.g. share offline)
        }
        for (const path of paths) found.push({ key: `${rootIndex}:${path}`, root: rootIndex, path, file: join(root, ...path.split("/")) });
      }),
    );
    found.sort((a, b) => a.root - b.root || (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));

    const loaded = await eachLimited(found, 16, async (f) => {
      let mtimeMs: number;
      try {
        mtimeMs = (await stat(f.file)).mtimeMs;
      } catch {
        return undefined;
      }
      const cached = this.cache.get(f.key);
      if (cached && cached.mtimeMs === mtimeMs) return { key: f.key, root: f.root, cached };
      return { key: f.key, root: f.root, cached: { mtimeMs, entry: await this.load(f.root, f.path, f.file, mtimeMs) } as Cached };
    });

    const next: CatalogEntry[] = [];
    const allowed = new Set<string>();
    const cache = new Map<string, Cached>();
    for (const l of loaded) {
      if (!l) continue;
      cache.set(l.key, l.cached);
      next.push(l.cached.entry);
      allowed.add(l.key);
      for (const asset of l.cached.entry.assets) allowed.add(`${l.root}:${asset}`);
    }
    const changed = next.length !== this.list.length || next.some((e, i) => e !== this.list[i]);
    this.cache = cache;
    this.list = next;
    this.allowed = allowed;
    if (changed) this.body = undefined;

    // Asset checks for new or changed manifests; problems go to the log, not the index.
    const unchecked = [...cache.values()].filter((c) => !c.checked && c.entry.valid);
    const warned: CatalogEntry[] = [];
    await eachLimited(unchecked, 4, async (c) => {
      const e = c.entry;
      const onDisk = e.assets.map((a) => ({ shown: a, file: join(this.roots[e.root]!, ...a.split("/")) }));
      const problems = [...(await checkAssetFiles(onDisk)), ...(await checkIgnoreFiles(onDisk))];
      e.warnings = [...(e.warnings ?? []), ...problems];
      if (e.warnings.length === 0) delete e.warnings;
      else warned.push(e);
      c.checked = true;
    });
    if (warned.length) this.onWarnings?.(warned);
  }

  private async load(root: number, path: string, file: string, mtimeMs: number): Promise<CatalogEntry> {
    const id = createHash("sha1").update(`${root}:${path}`).digest("hex").slice(0, 16);
    const base: CatalogEntry = {
      id,
      root,
      path,
      url: `/v1/files/${root}/${encodePath(path)}`,
      valid: false,
      hasMenus: false,
      media: [],
      assets: [],
      modified: new Date(mtimeMs).toISOString(),
    };
    let text: string;
    try {
      text = await readFile(file, "utf8");
    } catch (e) {
      return { ...base, errors: [`Unreadable: ${(e as Error).message}`] };
    }
    const result = parseManifest(text);
    if (!result.ok) return { ...base, errors: result.errors.map((e) => `${e.path || "/"}: ${e.message}`) };
    const m = result.manifest!;
    const dir = posix.dirname(path);
    const inRoot = (rel: string): string | undefined => {
      const p = posix.normalize(dir === "." ? rel : `${dir}/${rel}`);
      return p.startsWith("../") || p === ".." ? undefined : p;
    };
    const isRelative = (ref: string) => !/^[a-z][a-z0-9+.-]*:/i.test(ref) && !ref.startsWith("/");
    const assets = menuAssets(m).map(inRoot).filter((p): p is string => p !== undefined);
    const warnings: string[] = [];
    if (m.version !== FORMAT_VERSION && Number(m.version.split(".")[1]) > Number(FORMAT_VERSION.split(".")[1]))
      warnings.unshift(`Written for ${m.version}; this server reads ${FORMAT_VERSION}. Newer fields are ignored.`);
    return {
      ...base,
      valid: true,
      version: m.version,
      title: m.title,
      source: m.source,
      hasMenus: !!m.menus && Object.keys(m.menus).length > 0,
      media: Object.entries(m.media).map(([mid, media]) => ({
        id: mid,
        kind: media.kind,
        label: media.label,
        ref: media.ref,
        path: typeof media.ref === "string" && isRelative(media.ref) ? inRoot(media.ref) : undefined,
      })),
      assets,
      ...(warnings.length ? { warnings } : {}),
    };
  }
}
