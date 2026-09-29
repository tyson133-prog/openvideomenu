# Roku: ovmplayer beta channel

A **beta channel** lets up to 20 testers install the app from a link, without Roku's certification. It
expires 120 days after it's created, and needs no store listing. A public channel (certification and
listing) can follow at launch.

## Signing key (once)

Roku packages are signed on a Roku in developer mode, with a key that lives on that device.

1. With the TV in developer mode, run `telnet 192.168.4.53 8080` and type `genkey`.
2. It prints a **DevID** and a **password**. Keep the password with your other release secrets, and
   set it for the package tool:
   `setx ROKU_SIGNING_PASSWORD "<password>"` (Windows) or export it in your shell.
3. Keep the first signed `.pkg` too: on another Roku, `rekey` with that package and password loads the
   same key. **Every update to a channel must be signed with the same key.**

(`genkey` makes a new key each time it runs. Run it only once.)

## Each version

```sh
cd apps/roku
npm run package -- --version 0.1.2 --sign     # builds, installs on the TV, signs, downloads the .pkg
```

The `.pkg` lands in `apps/roku/out/`. The version must be higher than the last upload. The release
package leaves out the conformance cases (dev test mode).

## Developer Dashboard

1. developer.roku.com → **Manage channels** → **Add channel** → *Beta channel*.
2. Name: **ovmplayer** · upload the `.pkg`.
3. Add testers by the email addresses of their Roku accounts (up to 20). They get a link that adds the
   channel to their Roku; it appears after the next channel update check (Settings → System →
   Software update).
4. For later versions: upload the new `.pkg` to the same beta channel.

Beta channels stop working 120 days after creation; create a new one (same key) if testing runs
longer, or publish the public channel.

## Listing text (for the public channel later)

**Name:** ovmplayer · **Category:** Movies & TV / Special interest

**Short description:**
> Full menus for the films and shows in your Plex or Jellyfin library.

**Description:**
> ovmplayer brings back the menu. Films in your own Plex or Jellyfin library that have an Open Video
> Menu open straight into it: backgrounds, scene selection, special features, and audio and subtitle
> choices. Everything else plays as usual. Needs your own Plex or
> Jellyfin server; no ads, no tracking, no account with us. Learn more at openvideomenu.org.

**Notes for certification (public channel):** it's a client for the user's own media server, so
reviewers need a test server; offer the Jellyfin demo server or a test server with the Sintel example.
Roku requires channels to behave well with deep links and the Home key; check the current Roku
certification checklist before submitting.

Artwork for the public listing (channel poster and so on) is sized by the dashboard at submission time;
`apps/roku/images/` and `store-art/` hold the source art.
