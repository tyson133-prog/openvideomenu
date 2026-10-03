import { BUNDLE_EXTENSION, BUNDLE_MANIFEST_NAME } from "./constants.js";
import type { MediaRef } from "./types.js";

/**
 * Resolves a media reference to something the host can play. Hosts register resolvers for their
 * own schemes (e.g. "jellyfin"); string refs resolve against the manifest's location.
 */
export type MediaResolver = (ref: Exclude<MediaRef, string>) => string | undefined;

/**
 * Resolve a relative path or URL against the manifest URL. Relative paths use forward slashes and
 * may contain spaces and other characters that are percent-encoded here.
 */
export function resolvePath(path: string, manifestUrl: string | URL): string {
  return new URL(path, manifestUrl).href;
}

/**
 * Resolve a media ref. Paths resolve like asset paths, except in a bundle: there they resolve against
 * the folder that contains the bundle (spec §2.1), since the bundle holds the menus, not the videos.
 * A bundle's manifest is read as `<folder>/<Title>.ovmz/manifest.json`.
 */
export function resolveMedia(
  ref: MediaRef,
  manifestUrl: string | URL,
  resolvers: Record<string, MediaResolver> = {},
): string | undefined {
  if (typeof ref === "string") return resolvePath(ref, mediaBase(manifestUrl));
  return resolvers[ref.scheme]?.(ref);
}

/** The URL media paths resolve against: the manifest's, or for a bundle's manifest, the bundle's. */
function mediaBase(manifestUrl: string | URL): string {
  const url = new URL(manifestUrl);
  const parts = url.pathname.split("/");
  const bundle = parts[parts.length - 2];
  if (parts.length >= 3 && parts[parts.length - 1] === BUNDLE_MANIFEST_NAME &&
      decodeURIComponent(bundle!).toLowerCase().endsWith(BUNDLE_EXTENSION)) {
    url.pathname = parts.slice(0, -1).join("/"); // ".../X.ovmz": paths then resolve beside it
    url.search = "";
    url.hash = "";
  }
  return url.href;
}
