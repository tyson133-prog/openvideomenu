import { createHash } from "node:crypto";
import { readFileSync, statSync } from "node:fs";
import { isAbsolute, join, posix, relative, resolve } from "node:path";
import { FORMAT_VERSION, menuAssets, parseManifest, type MediaRef } from "@menustream/core";
import { findManifests } from "./files.js";

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
  version?: string;
  title?: { name: string; year?: number; ids?: Record<string, string | undefined> };
  source?: { kind: string; generator?: string };
  hasMenus: boolean;
  media: CatalogMedia[];
  /** Menu assets, relative to the media root. */
  assets: string[];
  modified: string;
}

interface Cached {
  mtimeMs: number;
  entry: CatalogEntry;
}

/** Encode each path segment for a URL, keeping the slashes. */
export const encodePath = (path: string) => path.split("/").map(encodeURIComponent).join("/");

/**
 * The manifests under the media roots, and the exact set of files that may be served: the manifests
 * themselves plus the menu assets they name. Nothing else — in particular no media — is reachable.
 */
export class Catalog {
  private cache = new Map<string, Cached>();
  private scannedAt = 0;
  private list: CatalogEntry[] = [];
  private allowed = new Set<string>();

  constructor(
    readonly roots: string[],
    private readonly ttlMs = 5 * 60_000,
  ) {
    this.roots = roots.map((r) => resolve(r));
  }

  entries(force = false): CatalogEntry[] {
    if (force || Date.now() - this.scannedAt > this.ttlMs) this.scan();
    return this.list;
  }

  /** Absolute path of an allowed file, or undefined. `path` is root-relative with forward slashes. */
  resolveFile(root: number, path: string): string | undefined {
    this.entries();
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

  scan() {
    const next: CatalogEntry[] = [];
    const allowed = new Set<string>();
    const seen = new Set<string>();
    this.roots.forEach((root, rootIndex) => {
      let paths: string[];
      try {
        paths = findManifests(root);
      } catch {
        return; // root missing or unreadable (e.g. share offline)
      }
      for (const path of paths) {
        const key = `${rootIndex}:${path}`;
        seen.add(key);
        const file = join(root, ...path.split("/"));
        let mtimeMs: number;
        try {
          mtimeMs = statSync(file).mtimeMs;
        } catch {
          continue;
        }
        const cached = this.cache.get(key);
        const entry = cached && cached.mtimeMs === mtimeMs ? cached.entry : this.load(rootIndex, path, file, mtimeMs);
        this.cache.set(key, { mtimeMs, entry });
        next.push(entry);
        allowed.add(key);
        for (const asset of entry.assets) allowed.add(`${rootIndex}:${asset}`);
      }
    });
    for (const key of this.cache.keys()) if (!seen.has(key)) this.cache.delete(key);
    this.list = next;
    this.allowed = allowed;
    this.scannedAt = Date.now();
  }

  private load(root: number, path: string, file: string, mtimeMs: number): CatalogEntry {
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
      text = readFileSync(file, "utf8");
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
      assets: menuAssets(m).map(inRoot).filter((p): p is string => p !== undefined),
      ...(m.version !== FORMAT_VERSION && Number(m.version.split(".")[1]) > Number(FORMAT_VERSION.split(".")[1])
        ? { errors: [`Written for ${m.version}; this server reads ${FORMAT_VERSION}. Newer fields are ignored.`] }
        : {}),
    };
  }
}
