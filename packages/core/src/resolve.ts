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

export function resolveMedia(
  ref: MediaRef,
  manifestUrl: string | URL,
  resolvers: Record<string, MediaResolver> = {},
): string | undefined {
  if (typeof ref === "string") return resolvePath(ref, manifestUrl);
  return resolvers[ref.scheme]?.(ref);
}
