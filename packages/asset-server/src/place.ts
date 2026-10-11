import type { Title } from "@openvideomenu/core";

/**
 * A menu's season and volume as its path names them, for manifests that don't say (spec §3.1): "Season 05",
 * "Specials" (season 0), "Disc 2" (or "Disk 2"), or both at once as "S05D2". The name nearest the file wins:
 * "Show/Season 05/Disc 2/Disc 2.ovmz" is season 5, volume 2.
 */
export function placeFromPath(path: string): { season?: number; volume?: number } {
  let season: number | undefined;
  let volume: number | undefined;
  for (const name of path.split("/").reverse()) {
    const both = /\bS(\d{1,3})[ ._-]?D(\d{1,3})\b/i.exec(name);
    if (both) {
      season ??= Number(both[1]);
      if (Number(both[2]) >= 1) volume ??= Number(both[2]);
    }
    const s = /\bseason[ ._-]*(\d{1,3})\b/i.exec(name);
    if (s) season ??= Number(s[1]);
    else if (/^specials$/i.test(name)) season ??= 0;
    const v = /\bdis[ck][ ._-]*(\d{1,3})\b/i.exec(name);
    if (v && Number(v[1]) >= 1) volume ??= Number(v[1]);
  }
  return { ...(season !== undefined ? { season } : {}), ...(volume !== undefined ? { volume } : {}) };
}

/** The manifest's title, with season and volume from its path where the manifest has none. */
export function titleWithPlace(title: Title, path: string): Title {
  if (title.season !== undefined && title.volume !== undefined) return title;
  const place = placeFromPath(path);
  return {
    ...title,
    ...(title.season === undefined && place.season !== undefined ? { season: place.season } : {}),
    ...(title.volume === undefined && place.volume !== undefined ? { volume: place.volume } : {}),
  };
}
