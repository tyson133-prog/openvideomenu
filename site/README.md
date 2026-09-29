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

| Page | Placeholder |
|---|---|
| index | DVD Tamer link and release date |
| format | Links to the published spec, schema, server API and DVD profile |
| players | Google Play, APK download, Downloader code, Roku channel and beta link, Jellyfin plugin repository URL, menu-server setup guide, example title download, demo video |
| developers | Conformance suite link, npm and NuGet links |
| about | Repository, issue tracker, forum, contact email |
| privacy | Effective date, contact email, host name and log retention |

Replace a placeholder with a real link (`<a class="btn btn-primary" href="…">` for buttons).

## Images

`assets/img/` holds the mark and favicon (SVG) and screenshots of the Sintel example running on a TV.
Sintel © Blender Foundation, durian.blender.org, CC BY 3.0: keep the credit in the footer. Don't use
screenshots of commercial films.
