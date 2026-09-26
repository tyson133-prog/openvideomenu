import { parseManifest } from "@menustream/core";
import { MenuPlayer, type PlayerOptions } from "./player.js";
import { injectStyles } from "./styles.js";

export type LoadOptions = Omit<PlayerOptions, "manifest" | "manifestUrl">;

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
  for (const w of result.warnings) console.warn(`[menustream] ${w.path || "/"}: ${w.message}`);
  if (!result.ok) {
    showProblems(container, "This menu file has problems", result.errors.map((e) => `${e.path || "/"}: ${e.message}`));
    return undefined;
  }
  const player = new MenuPlayer(container, { ...options, manifest: result.manifest!, manifestUrl: absolute });
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
