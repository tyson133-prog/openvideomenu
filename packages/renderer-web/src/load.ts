import { parseManifest, type Session } from "@openvideomenu/core";
import { MenuPlayer, type PlayerOptions } from "./player.js";
import { injectStyles } from "./styles.js";

export type LoadOptions = Omit<PlayerOptions, "manifest" | "manifestUrl" | "onSession"> & {
  /** The version of the manifest `session` was kept for (from `onSession`). Another version drops it (spec §6.5.1). */
  sessionVersion?: string;
  /** Level 3: the session changed; keep it with the manifest's version, to pass back as session and sessionVersion. */
  onSession?: (session: Session, version: string) => void;
};

/** A manifest's version for kept sessions: a hash of its text (FNV-1a, 32 bits) and its length. */
export function manifestVersion(text: string): string {
  let hash = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) {
    hash ^= text.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return `${hash.toString(16).padStart(8, "0")}-${text.length.toString(16)}`;
}

/**
 * Fetch, validate and start a manifest. Validation problems are shown on screen instead of a menu,
 * because a player that guesses at a broken manifest teaches authors the wrong thing.
 */
export async function loadPlayer(container: HTMLElement, manifestUrl: string, options: LoadOptions = {}): Promise<MenuPlayer | undefined> {
  const absolute = new URL(manifestUrl, document.baseURI).href;
  let text: string;
  try {
    const response = await fetch(absolute);
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
    text = await response.text();
  } catch (e) {
    showProblems(container, `Couldn't load ${absolute}`, [(e as Error).message]);
    return undefined;
  }
  const result = parseManifest(text);
  for (const w of result.warnings) console.warn(`[openvideomenu] ${w.path || "/"}: ${w.message}`);
  if (!result.ok) {
    showProblems(container, "This menu file has problems", result.errors.map((e) => `${e.path || "/"}: ${e.message}`));
    return undefined;
  }
  // A session kept for another version of the manifest means nothing to this one.
  const version = manifestVersion(text);
  const { sessionVersion, onSession, ...rest } = options;
  const player = new MenuPlayer(container, {
    ...rest,
    session: sessionVersion === version ? rest.session : undefined,
    onSession: onSession && ((session) => onSession(session, version)),
    manifest: result.manifest!,
    manifestUrl: absolute,
  });
  player.start();
  return player;
}

function showProblems(container: HTMLElement, heading: string, lines: string[]) {
  injectStyles(container.ownerDocument);
  const el = document.createElement("div");
  el.className = "ms-root";
  const panel = document.createElement("div");
  panel.className = "ms-fallback";
  const h1 = document.createElement("h1");
  h1.textContent = heading;
  panel.appendChild(h1);
  for (const line of lines) {
    const p = document.createElement("p");
    p.textContent = line;
    panel.appendChild(p);
  }
  el.appendChild(panel);
  container.appendChild(el);
}
