# Roku: ovmplayer in the Roku Streaming Store

The public channel goes through Roku's certification: automated checks you can run as often as you like,
then a review by Roku that starts only when you schedule publishing. It goes live on the date you pick.
Testers can try it before then from a **beta channel** (below).

## Signing key (once)

Roku packages are signed on a Roku in developer mode, with a key that lives on that device.

1. With the TV in developer mode, run `telnet 192.168.4.53 8080` and type `genkey`.
2. It prints a **DevID** and a **password**. Keep the password with your other release secrets, and
   set it for the package tool:
   `setx ROKU_SIGNING_PASSWORD "<password>"` (Windows) or export it in your shell.
3. Keep the first signed `.pkg` too: on another Roku, `rekey` with that package and password loads the
   same key. **Every update to a channel must be signed with the same key.**

(`genkey` makes a new key each time it runs. Run it only once.)

The TV must be linked to the Roku developer account and online when it signs.

## Each version

```sh
cd apps/roku
npm run package -- --version 0.1.4 --sign     # builds, installs on the TV, signs, downloads the .pkg
```

The `.pkg` lands in `apps/roku/out/`. The version must be higher than the last upload. The release
package leaves out the conformance cases (dev test mode). The dashboard takes the signed `.pkg`, not the
zip.

## Certification (public channel)

What the app already does for the criteria (October 2026): `rsg_version=1.3`, low-memory events
(roAppMemoryMonitor and the device's general memory level), `AppLaunchComplete` once the library is on
screen with the first-run questions wrapped in `AppDialogInitiate`/`AppDialogComplete`, and deep links
at launch and through roInput (also while something plays).

1. **Static Analysis:** upload the `.pkg`; fix every error. Set the **minimum Roku OS to 15.1** when
   uploading (a dashboard setting).
2. **Test credentials:** the demo server, which runs all the time.
   - Choose **Jellyfin**, then **Type an address…**: `https://demo.openvideomenu.org`
   - **Use username and password:** user `review`; the password is kept outside the repo.
3. **Deep-link test values** (Jellyfin IDs on the demo server; `deploy/demo-server/configure.mjs` prints
   them again):
   - Sintel, which opens its menu: `contentId=8abb00881d1cf44cda4357f3c253c369&mediaType=movie`
   - Example Show, which opens its screen: `contentId=39bcdf08f9551e6f30cdc6660c99073d&mediaType=series`
   - An episode, which plays: `contentId=cc0161de1989260ab600e242abf35707&mediaType=episode`
4. **Sign-in and sign-out scripts** for App Behavior Analysis (Roku Remote Tool):
   - Sign in from first launch: Down, Select (Jellyfin); Select (Type an address…); type the address;
     Down ×4, Select (Connect); Select (Use username and password); type `review`; Down ×4, Select;
     type the password; Down ×4, Select. The library shows.
   - Sign out: from the library, `*` (Options) signs out and returns to "Where are your movies?".
5. **Schedule publishing** for launch day, at least a week or two ahead: Roku's review starts only once
   it's scheduled, and a failed review needs time for a resubmission. Not on a Friday or weekend; the
   earliest is two business days out.

## Beta channel (testers before launch)

A beta channel lets up to 20 testers install the app from a link, without certification. It expires 120
days after it's created.

1. developer.roku.com → **Manage channels** → **Add channel** → *Beta channel*.
2. Name: **ovmplayer** · upload the `.pkg`.
3. Add testers by the email addresses of their Roku accounts (up to 20). They get a link that adds the
   channel to their Roku; it appears after the next channel update check (Settings → System →
   Software update).
4. For later versions: upload the new `.pkg` to the same beta channel.

## Listing text

**Name:** ovmplayer · **Category:** Movies & TV / Special interest

**Short description:**
> Full menus for the films and shows in your Plex or Jellyfin library.

**Description:**
> ovmplayer brings back the menu. Films in your own Plex or Jellyfin library that have an Open Video
> Menu open straight into it: backgrounds, scene selection, special features, and audio and subtitle
> choices. Everything else plays as usual. Needs your own Plex or
> Jellyfin server; no ads, no tracking, no account with us. Learn more at openvideomenu.org.

Artwork for the listing (channel poster and so on) is sized by the dashboard at submission time;
`apps/roku/images/` and `store-art/` hold the source art.
