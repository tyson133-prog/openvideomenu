#!/usr/bin/env node
// Bundles the menu server (packages/asset-server) and its dependencies into one file,
// packaging/build/menu-server.mjs, which the installers ship next to a Node.js runtime.
//
//   node packaging/bundle.mjs
import { build } from "esbuild";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
await build({
  entryPoints: [join(root, "packages/asset-server/src/main.ts")],
  outfile: join(root, "packaging/build/menu-server.mjs"),
  bundle: true,
  platform: "node",
  target: "node22",
  format: "esm",
  // Some dependencies are CommonJS and call require(): give the ES module one.
  banner: { js: "import { createRequire as __createRequire } from 'node:module'; const require = __createRequire(import.meta.url);" },
  legalComments: "inline",
  logLevel: "warning",
});
console.log("packaging/build/menu-server.mjs");
