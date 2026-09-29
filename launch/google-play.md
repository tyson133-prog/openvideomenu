# Google Play: ovmplayer for Google TV and Android TV

Package `com.ovmplayer.tv` · developer name **Tyson Treasure** · TV only (the app requires Leanback,
so Play offers it only on TVs).

## Store listing

**App name** (max 30): `ovmplayer: Movie & TV Menus`

**Short description** (max 80):
> Full menus for the films and shows in your Plex or Jellyfin library.

**Full description** (max 4000):

> ovmplayer brings back the menu. Open a film from your own Plex or Jellyfin library and, if it has
> an Open Video Menu, it opens straight into it: animated backgrounds, scene selection, special
> features, and audio and subtitle choices. Everything else plays as usual.
>
> Menus are small files that sit next to your films in the folders Plex and Jellyfin already use. They
> point at the video you already have; ovmplayer never copies, converts or unlocks anything.
>
> WHAT YOU GET
> • Full menus with highlights and remote-control navigation
> • Motion menus with music, intros and transitions (any button skips)
> • Scene selection, special features and play-all
> • Pick up where you left off: the film or an extra, right where you stopped
> • Your whole library too: movies and shows, with progress synced to your server
> • AC-3 and DTS soundtracks, passed through to your receiver where supported
>
> WHAT YOU NEED
> • A Plex or Jellyfin server of your own (or one you've been invited to)
> • For menus: the Open Video Menu plugin for Jellyfin, or the small menu server that runs beside Plex
> • Films with Open Video Menu menus next to them. Two example titles are available to try.
>
> PRIVATE BY DESIGN
> No ads, no analytics, no tracking, no account with us. The app talks only to your media server (and
> to plex.tv to sign in to Plex).
>
> ovmplayer is a proof-of-concept player for Open Video Menu, an open, royalty-free format for menus. Learn more at openvideomenu.org.
>
> Plex and Jellyfin are trademarks of their owners. ovmplayer is an independent app, not affiliated
> with or endorsed by them.

**Category:** Video Players & Editors · **Tags:** Media player, Movies & TV (pick from Play's list)

**Contact:** email: _(to fill)_ · website: https://openvideomenu.org · privacy policy:
https://openvideomenu.org/privacy.html

## Graphics (in `store-art/`)

| Asset | File | Required size |
|---|---|---|
| App icon | `play-icon-512.png` | 512 × 512 PNG |
| Feature graphic | `play-feature-graphic-1024x500.png` | 1024 × 500 |
| Android TV banner | `play-tv-banner-1280x720.png` | 1280 × 720 |
| TV screenshots | `tv-screenshot-*.png` | 1920 × 1080, at least 1 |

Screenshots show the Sintel example (Blender Foundation, CC BY 3.0). Don't add screenshots of
commercial films or of library screens with their posters.

## App content (Policy → App content)

These are declarations you make; the answers below match what the app does today (checked against the
code on 2026-09-28). Re-check if the app changes.

- **Privacy policy:** https://openvideomenu.org/privacy.html
- **Ads:** No.
- **App access:** *All or some functionality is restricted* (the app needs a media server). Give
  reviewers a Jellyfin test server and account: the app signs in with a username and password as
  well as Quick Connect. Best is a test server with the Sintel example, so they see a menu;
  otherwise `https://demo.jellyfin.org/stable`, user `demo`, no password (check it's still up).
  > Choose Jellyfin, enter the server address `<test server>`, and sign in with username `<user>`
  > and password `<password>`. Open "Sintel" in the Menus row to see its menu.
- **Content rating (IARC questionnaire):** category *All other app types*; no violence, sexuality,
  language or controlled substances in the app itself; no user interaction or chat between users; no
  sharing of location; no purchases. It plays the user's own media. Expect a rating of Everyone / PEGI 3.
- **Target audience:** 18 and over (keeps the app out of the Families programme; it isn't made for
  children).
- **News app:** No. **Health, financial, government:** No.
- **Data safety:**
  - *Does your app collect or share any of the required user data types?* **No.** The app sends
    nothing to the developer. What it sends goes to servers the user chooses and signs in to (their
    Plex or Jellyfin server, and plex.tv for Plex sign-in) as part of the service the user asked for,
    like any Plex or Jellyfin client.
  - *Is all data encrypted in transit?* Answer only if asked after the "No" above; connections to
    plex.tv are HTTPS, but home servers are often plain HTTP on the local network.
  - *Can users request data deletion?* Not applicable: nothing is stored off the device. Signing out
    removes the token from the device.

  Google's definitions of "collected" can be read strictly; if in doubt, declare *Account info → User
  IDs* (the media-server token) as collected, not shared, required for app functionality, and not
  retained by the developer.

## Testing tracks

- New personal accounts: **closed testing with at least 12 testers, opted in for 14 continuous days**,
  before you can apply for production.
- Add testers by email list or Google Group; they opt in with the testing link, then install from Play
  on their TV (or on the web Play Store, sending it to the TV).
- Upload `ovmplayer-androidtv-<version>.aab` from the draft GitHub release. The first upload asks you
  to enrol in **Play App Signing**: accept; the key from the release workflow becomes the upload key.

## Before production

- Android TV quality review: D-pad navigation everywhere, no touch-only screens, a banner, the app
  appears in the TV launcher. (All true today; re-check after UI changes.)
- `targetSdk` must stay within Play's current requirement (36 today).
