# openvideomenu.org

The project's website: plain HTML and one stylesheet, with no build step, no JavaScript, and nothing
loaded from other sites (no fonts, scripts, analytics or cookies; the privacy page says so, so keep it
that way).

## Hosting

Upload the contents of this folder to any static host (GitHub Pages, Cloudflare Pages, Netlify, or a
web server). Links between pages are relative, so the site also works from a local folder.

- `404.html` is the not-found page; point the host at it.
- Canonical and social-card URLs assume `https://openvideomenu.org/`.
- Redirect openvideomenu.com and the plural domains to openvideomenu.org.

## Placeholders

Anything that doesn't exist yet is marked in the pages with a dashed "Placeholder" box
(`class="tbd"`) or a "Soon" button (`class="btn tbd-link"`). Find them all with:

```sh
grep -n "tbd" *.html
```

| Where | Placeholder |
|---|---|
| Set it up | Google Play, Roku channel, dvdtamer.com link |
| 1. See it | Demo video |
| 2. Get the players | Google Play, APK download, Roku channel |
| 3. Make menus | dvdtamer.com link |

Replace a placeholder with a real link (`<a class="btn btn-primary" href="…">` for buttons).

## What the site is

One page, a how-to: what Open Video Menu is, then four numbered steps (see it, get the players, make
menus, implement it). Keep it that way: details belong in the specification and the READMEs it links
to, not on the site. `privacy.html` and `terms.html` (the app stores require them) and `404.html` are the only other pages, besides the
specification (below).

## The specification

`spec/<version>/` holds the specification as web pages (the spec, server API, DVD profile, compatibility,
license, and the JSON Schema), built from the Markdown in the repo's `spec/` folder. After changing the
spec, run `npm run site:spec` and commit the pages. Each version keeps its address: a new version gets a new
folder and `spec/` points at it.

## The Linux installer

`install/menu-server.sh` is the menu server's Linux installer (`curl -fsSL https://openvideomenu.org/install/menu-server.sh | sudo sh`).
It's a copy of [`packaging/linux/install.sh`](../packaging/linux/install.sh): change that one and copy it here; the
release workflow fails if they differ. Serve it as plain text.

## The repository's address

Links into the code use `https://github.com/tyson133-prog/openvideomenu` with branch `main`. If the
public repository or branch differs, change it in `index.html` (step 4 and the footer) and in
`tools/build-spec.mjs` (`REPO`), then rebuild the spec pages.

## Images

`assets/img/` holds the mark and favicon (SVG) and a screenshot of the Sintel example running on a TV.
Sintel © Blender Foundation, durian.blender.org, CC BY 3.0: keep the credit in the footer. Don't use
screenshots of commercial films.
