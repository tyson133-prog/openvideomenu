# Soft launch kit

Everything needed to put the players in testers' hands: builds, store listings, artwork and the steps
in order. **How open the soft launch is (public repo or invite-only) is decided after the DVD Tamer
work;** nothing here publishes anything by itself.

| File | What it is |
|---|---|
| [`google-play.md`](google-play.md) | Play Console listing text, Data safety answers, content rating, reviewer access |
| [`roku.md`](roku.md) | Roku: signing key, packaging, certification (test account, deep links, scripts), beta channel, listing text |
| [`store-art/`](store-art) | Play icon (512), feature graphic (1024×500), TV banner (1280×720), TV screenshots (1920×1080). `store-art/src/` has the HTML they're rendered from. |
| [`../site/`](../site) | The website, including the privacy policy both stores require |
| [`../.github/workflows/release.yml`](../.github/workflows/release.yml) | Builds every artifact from a version tag into a **draft** GitHub release |
| [`../.github/workflows/publish-nuget.yml`](../.github/workflows/publish-nuget.yml) | Pushes the .NET packages to nuget.org when that release is published |
| [`../deploy/demo-server/`](../deploy/demo-server) | The demo server (https://demo.openvideomenu.org) that store reviewers sign in to |

## Checklist

### Once

- [ ] **Android signing key.** I generate the keystore and hand it to you; you store it safely (a
  password manager and an offline copy) and add four GitHub secrets: `ANDROID_KEYSTORE_BASE64`,
  `OVM_KEYSTORE_PASSWORD`, `OVM_KEY_ALIAS`, `OVM_KEY_PASSWORD`. Losing it means the APK can't be updated
  in place. (Google Play re-signs with its own key once enrolled in Play App Signing; this key is then
  the upload key.)
- [ ] **Roku signing key.** Run `genkey` on the Roku (see [`roku.md`](roku.md)). Keep the password it
  prints and the first signed `.pkg`: updates must be signed with the same key.
- [ ] **Google Play Console account**, personal, in your name ($25, ID verification). New personal
  accounts must run a **closed test with at least 12 testers for 14 days** before production.
- [ ] **Host the website** (see [`../site/README.md`](../site/README.md)) and fill its placeholders,
  at least the privacy page's date and contact email: both stores link to it.
- [ ] **Contact email** for listings and the privacy page.
- [ ] **nuget.org account**, an API key for the `OpenVideoMenu*` packages as the `NUGET_API_KEY` secret,
  and a request to reserve the `OpenVideoMenu.*` ID prefix.
- [x] **Demo server for store reviewers:** https://demo.openvideomenu.org (`deploy/demo-server`), account
  `review`.

### Each release

1. Tag the version: `git tag v0.1.2 && git push origin v0.1.2`. The workflow tests everything and
   drafts a release with the APK, App Bundle (AAB), unsigned Roku zip, Jellyfin plugin zip and
   repository manifest, the NuGet packages (OpenVideoMenu, OpenVideoMenu.Dvd, the ovm-dvd tool), and
   `openvideomenu-examples.zip` (Sintel and the Example Show, which the website links to).
2. **Roku:** sign on the TV: `cd apps/roku && npm run package -- --version 0.1.2 --sign`, then upload
   the `.pkg` to the beta channel, or to the public channel's certification (see [`roku.md`](roku.md):
   schedule publishing a week or two before the day it should go live).
3. **Google Play:** upload the `.aab` to the closed-testing track.
4. **Jellyfin repository:** host `jellyfin-repository.json` (e.g. on the website) where testers add it;
   its download URL points at the release's plugin zip, which must be public by then.
5. **Publish the draft release** when the testers should get it. Publishing it also pushes the NuGet
   packages to nuget.org (permanent: versions can be unlisted, never deleted).

## Before inviting testers

- [x] Restore Jellyfin `MinResumeDurationSeconds` 10 → 300 on the test container (test setting).
- [ ] Remove the Hyper-V firewall rules `OVM-WSL-Test-TCP` and `OVM-WSL-Test-mDNS` (if not done).
- [ ] Decide the public branch (`main`) and commit email.
- [x] Example titles testers can download (Sintel and Example Show): a release asset, linked from the
  website, and the public demo account on demo.openvideomenu.org.
- [ ] Test the Jellyfin plugin repository install from a clean Jellyfin (add repository → install).
- [ ] Fire TV: untested; either test it or keep the site saying so.
- [x] A Jellyfin test server and account for store reviewers: the demo server (see [`google-play.md`](google-play.md#app-content-policy--app-content) and [`roku.md`](roku.md)).
