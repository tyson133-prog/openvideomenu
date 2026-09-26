import { readdirSync, statSync } from "node:fs";
import { join, relative, sep } from "node:path";
import { MANIFEST_SUFFIX } from "@menustream/core";

/** Manifest files under `root` (or `root` itself when it is a manifest), as forward-slash relative paths. */
export function findManifests(root: string, maxDepth = 4): string[] {
  if (statSync(root).isFile()) return [root];
  const found: string[] = [];
  const walk = (dir: string, depth: number) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      if (entry.name.startsWith(".") || entry.name === "node_modules") continue;
      const full = join(dir, entry.name);
      if (entry.isDirectory() && depth < maxDepth) walk(full, depth + 1);
      else if (entry.isFile() && entry.name.toLowerCase().endsWith(MANIFEST_SUFFIX)) found.push(full);
    }
  };
  walk(root, 0);
  return found.sort().map((f) => relative(root, f).split(sep).join("/"));
}
