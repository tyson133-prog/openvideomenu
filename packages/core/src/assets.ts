import type { Action, Button, Cue, Manifest, MenuKeys } from "./types.js";

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
    if (action.type === "play") {
      walkCues(action.cues);
      walkKeys(action.keys);
    }
    if (action.type === "playAll")
      for (const item of action.items) {
        walkCues(item.cues);
        walkKeys(item.keys);
      }
    if (action.type === "set") walkAction(action.then);
    if (action.type === "resume") walkAction(action.otherwise);
    if (action.type === "if") {
      walkAction(action.then);
      walkAction(action.else);
    }
    if (action.type === "do") action.steps.forEach(walkAction);
  };
  const walkButtons = (buttons: Button[] | undefined) => {
    for (const button of buttons ?? []) {
      for (const state of Object.values(button.states ?? {})) add(state?.image);
      walkAction(button.action);
    }
  };
  const walkKeys = (keys: MenuKeys | undefined) => {
    for (const action of Object.values(keys ?? {})) walkAction(action);
  };
  const walkCues = (cues: Cue[] | undefined) => {
    for (const cue of cues ?? []) {
      if ("at" in cue) walkAction(cue.action);
      else walkButtons(cue.buttons);
    }
  };

  for (const menu of Object.values(manifest.menus ?? {})) {
    add(menu.background?.image);
    add(menu.background?.video?.src);
    add(menu.background?.audio?.src);
    walkAction(menu.back);
    walkAction(menu.timeout?.action);
    walkKeys(menu.keys);
    walkButtons(menu.buttons);
  }
  walkAction(manifest.firstPlay);
  for (const action of Object.values(manifest.actions ?? {})) walkAction(action);
  walkKeys(manifest.keys);
  for (const group of manifest.fallback ?? []) for (const item of group.items ?? []) walkAction(item.action);
  for (const media of Object.values(manifest.media)) {
    for (const chapter of media.chapters ?? []) add(chapter.thumbnail);
    walkCues(media.cues);
    walkKeys(media.keys);
  }
  return [...found].sort();
}
