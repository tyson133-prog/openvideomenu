#!/usr/bin/env node
// Copies the repo's LICENSE and NOTICE into the package being packed (run as its "prepack"), so every
// published package carries them, as the Apache License requires (sections 4(a) and 4(d)).
import { copyFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
for (const name of ["LICENSE", "NOTICE"]) copyFileSync(join(root, name), join(process.cwd(), name));
