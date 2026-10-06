#!/usr/bin/env node
// Builds the specification's pages for the website from spec/*.md: site/spec/<version>/ (the spec, the server
// API, the DVD profile, compatibility and the license, plus the JSON Schema) and site/spec/index.html, which
// points at the current version. The Markdown in spec/ stays the source; run this after changing it and commit
// the pages, so the site folder is always ready to upload.
//
//   node site/tools/build-spec.mjs
//
// Pages of earlier versions are left as they are: each version keeps its address.

import { copyFileSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { dirname, join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { Marked } from "marked";

// Links from the spec into the code (../packages/…, ../conformance, …) go to the public repository.
// Set this when the repository and branch are public.
const REPO = "https://github.com/tyson133-prog/openvideomenu/blob/main/";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const specDir = join(root, "spec");
const main = readdirSync(specDir).find((f) => /^openvideomenu-v[\d.]+\.md$/.test(f));
if (!main) throw new Error("No spec/openvideomenu-v<version>.md");
const version = main.match(/v([\d.]+)\.md$/)[1];
const out = join(root, "site", "spec", version);

// Source file (relative to spec/) → page (relative to the version folder), and its title in the contents.
const pages = [
  { src: main, page: "index.html", title: `Specification ${version}` },
  { src: "server-api.md", page: "server-api.html", title: "Server API" },
  { src: "profiles/dvd.md", page: "dvd-profile.html", title: "DVD profile" },
  { src: "COMPATIBILITY.md", page: "compatibility.html", title: "Compatibility" },
  { src: "LICENSE.md", page: "license.html", title: "License" },
];
const schema = { src: "schema/manifest.schema.json", page: "manifest.schema.json", title: "JSON Schema" };
const pageOf = new Map([...pages, schema].map((p) => [p.src, p.page]));

/** GitHub's heading ids, so links written for the Markdown (#64-named-actions-level-3) keep working. */
function slug(text, seen) {
  let s = text.toLowerCase().trim().replace(/<[^>]+>/g, "").replace(/[^\p{L}\p{N}\s-]/gu, "").replace(/\s/g, "-");
  const base = s;
  for (let n = 1; seen.has(s); n++) s = `${base}-${n}`;
  seen.add(s);
  return s;
}

const escapeHtml = (s) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");

/** A link written for the repository, as a link from the page made from `src`. */
function linkFor(href, src) {
  if (/^(https?:|mailto:|#)/.test(href)) return href;
  const [path, hash] = href.split("#");
  const target = join(dirname(src), path).replace(/\\/g, "/");
  const page = pageOf.get(target);
  if (page) return page === pageOf.get(src) && hash ? `#${hash}` : page + (hash ? `#${hash}` : "");
  // Anything else lives in the repository: link to it there.
  const inRepo = relative(root, join(specDir, target)).replace(/\\/g, "/");
  return REPO + inRepo + (hash ? `#${hash}` : "");
}

function render(src) {
  const seen = new Set();
  const contents = [];
  const marked = new Marked({
    gfm: true,
    renderer: {
      heading({ tokens, depth }) {
        const html = this.parser.parseInline(tokens);
        const id = slug(html, seen);
        if (depth === 2) contents.push({ id, html });
        return `<h${depth} id="${id}"><a class="anchor" href="#${id}">${html}</a></h${depth}>\n`;
      },
      link({ href, title, tokens }) {
        const text = this.parser.parseInline(tokens);
        return `<a href="${escapeHtml(linkFor(href, src))}"${title ? ` title="${escapeHtml(title)}"` : ""}>${text}</a>`;
      },
      table(token) {
        const cell = (c, tag) => {
          const align = c.align ? ` style="text-align:${c.align}"` : "";
          return `<${tag}${align}>${this.parser.parseInline(c.tokens)}</${tag}>`;
        };
        const head = `<tr>${token.header.map((c) => cell(c, "th")).join("")}</tr>`;
        const body = token.rows.map((r) => `<tr>${r.map((c) => cell(c, "td")).join("")}</tr>`).join("\n");
        return `<div class="table-wrap"><table><thead>${head}</thead><tbody>${body}</tbody></table></div>\n`;
      },
    },
  });
  const md = readFileSync(join(specDir, src), "utf8");
  return { html: marked.parse(md), contents };
}

const header = readFileSync(join(root, "site", "index.html"), "utf8").match(/<header class="site-header">[\s\S]*?<\/header>/)[0]
  .replace(/href="#(\w+)"/g, 'href="../../index.html#$1"')
  .replace(/href="index.html"/g, 'href="../../index.html"')
  .replace(/src="assets\//g, 'src="../../assets/');
const footer = readFileSync(join(root, "site", "index.html"), "utf8").match(/<footer class="site-footer">[\s\S]*?<\/footer>/)[0]
  .replace(/href="privacy.html"/g, 'href="../../privacy.html"');

function page({ title, body, contents, current }) {
  const nav = [...pages, schema]
    .map((p) => (p.page === current ? `<strong>${p.title}</strong>` : `<a href="${p.page}">${p.title}</a>`))
    .join(" · ");
  const toc = contents.length > 2
    ? `<details class="spec-toc" open><summary>Contents</summary><ul>${contents.map((c) => `<li><a href="#${c.id}">${c.html}</a></li>`).join("")}</ul></details>`
    : "";
  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>${escapeHtml(title)} · Open Video Menu ${version}</title>
  <meta name="description" content="Open Video Menu ${version}: ${escapeHtml(title)}.">
  <meta name="theme-color" content="#07080b">
  <link rel="icon" href="../../assets/img/favicon.svg" type="image/svg+xml">
  <link rel="stylesheet" href="../../assets/site.css">
</head>
<body>
<a class="skip" href="#main">Skip to content</a>

${header}

<main id="main">
  <section class="spec-page">
    <div class="wrap">
      <p class="small muted spec-nav">Open Video Menu ${version} · ${nav}</p>
      ${toc}
      <article class="prose spec">
${body}
      </article>
    </div>
  </section>
</main>

${footer}
</body>
</html>
`;
}

mkdirSync(out, { recursive: true });
for (const p of pages) {
  const { html, contents } = render(p.src);
  writeFileSync(join(out, p.page), page({ title: p.title, body: html, contents, current: p.page }));
  console.log(`site/spec/${version}/${p.page}`);
}
copyFileSync(join(specDir, schema.src), join(out, schema.page));
console.log(`site/spec/${version}/${schema.page}`);

// /spec/: the current version, and every version built so far.
const versions = readdirSync(join(root, "site", "spec"), { withFileTypes: true })
  .filter((d) => d.isDirectory()).map((d) => d.name)
  .sort((a, b) => b.localeCompare(a, undefined, { numeric: true }));
writeFileSync(join(root, "site", "spec", "index.html"), `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Specification · Open Video Menu</title>
  <meta http-equiv="refresh" content="0; url=${version}/">
  <link rel="canonical" href="https://openvideomenu.org/spec/${version}/">
</head>
<body>
<p>The current specification is <a href="${version}/">Open Video Menu ${version}</a>.</p>
<p>All versions: ${versions.map((v) => `<a href="${v}/">${v}</a>`).join(" · ")}</p>
</body>
</html>
`);
console.log("site/spec/index.html");
