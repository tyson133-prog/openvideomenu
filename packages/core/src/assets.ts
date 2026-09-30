import type { Action, Manifest } from "./types.js";

/**
 * Every menu asset a manifest names (backgrounds, highlight overlays, menu music, transitions,
 * chapter thumbnails), as written in the manifest. Media refs (the film, extras, episodes) are not
 * assets and are never included. Absolute URLs are skipped: only paths relative to the manifest are
 * returned, so servers can build an exact allowlist from them.
 */
export function menuAssets(manifest: Manifest): string[] {
  const found = new Set<string>();
  const add = (path: unknown) => {
    if (typeof path === "string" && path.length > 0 && !/^[a-z][a-z0-9+.-]*:/i.test(path) && !path.startsWith("/")) found.add(path);
  };
  const walkAction = (action: Action | undefined) => {
    if (!action) return;
    if (action.type === "goto") add(action.transition);
    if ((action.type === "play" || action.type === "playAll") && action.after) walkAction(action.after);
    if (action.type === "set") walkAction(action.then);
    if (action.type === "resume") walkAction(action.otherwise);
    if (action.type === "if") {
      walkAction(action.then);
      walkAction(action.else);
    }
    if (action.type === "do") action.steps.forEach(walkAction);
  };

  for (const menu of Object.values(manifest.menus ?? {})) {
    add(menu.background?.image);
    add(menu.background?.video?.src);
    add(menu.background?.audio?.src);
    walkAction(menu.timeout?.action);
    for (const button of menu.buttons ?? []) {
      for (const state of Object.values(button.states ?? {})) add(state?.image);
      walkAction(button.action);
    }
  }
  walkAction(manifest.firstPlay);
  for (const media of Object.values(manifest.media)) for (const chapter of media.chapters ?? []) add(chapter.thumbnail);
  return [...found].sort();
}
