# OpenVideoMenu.Dvd

DVD → Open Video Menu for .NET. It reads a DVD (an ISO image or a VIDEO_TS folder) and runs its menus and
programs the way a DVD player does. It translates them into an Open Video Menu manifest that plays the files
you ripped, makes the menu art, and checks the result against the disc.

**Players don't need this package.** They play Open Video Menu bundles with the core `OpenVideoMenu` SDK. This
package is for tools that turn DVDs into bundles: rippers, media-server plugins, archiving tools.

Status: being split out of DVD Tamer (see `../PLAN.md`). The public API is not settled yet.

- **Faithful by default:** the whole disc's logic, with art when ffmpeg is available.
- **Decrypted input:** the package doesn't remove CSS or any other copy protection.
- **License:** Apache-2.0.
