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
| 1. See it | Demo video |
| 2. Get the players | Google Play, APK download, Roku channel, Jellyfin plugin repository URL, menu-server setup guide |
| 3. Make menus | DVD Tamer link |
| 4. Implement it | Links to the specification, JSON Schema, conformance suite, server API and DVD profile (they wait on the public repository and branch) |
| Footer | Source code and contact links |
| privacy | Effective date, contact email, host name and log retention |

Replace a placeholder with a real link (`<a class="btn btn-primary" href="…">` for buttons).

## What the site is

One page, a how-to: what Open Video Menu is, then four numbered steps (see it, get the players, make
menus, implement it). Keep it that way: details belong in the specification and the READMEs it links
to, not on the site. `privacy.html` (both app stores require it) and `404.html` are the only other pages.

## Images

`assets/img/` holds the mark and favicon (SVG) and a screenshot of the Sintel example running on a TV.
Sintel © Blender Foundation, durian.blender.org, CC BY 3.0: keep the credit in the footer. Don't use
screenshots of commercial films.
