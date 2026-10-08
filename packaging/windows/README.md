# The menu server on Windows

`OpenVideoMenu-MenuServer-Setup.exe` installs the menu server as a Windows service that starts with the PC. It
includes everything it needs (Node.js and the server); nothing else to install.

## What the installer does

1. **Which media server:** Plex or Jellyfin, and its address (Plex: `http://127.0.0.1:32400`; Jellyfin:
   `http://127.0.0.1:8096`).
2. **Signs in to it,** to read its library folders:
   - Plex: the sign-in Plex already keeps on this PC, if Plex runs here. Otherwise a code to enter at
     plex.tv/link, or a token typed in.
   - Jellyfin: an administrator's user name and password, used once to make an API key. Only the key is kept.
3. **Shows the library folders** the menu server will follow. It reads them from the media server again by
   itself, so new libraries are found without running setup again.
4. **The service account.** Folders on this PC: Windows' built-in Local Service account. Folders on a network
   share (`\\server\share`) or a mapped drive: a Windows user, because services can only read shares as a user.
   The account Plex or Jellyfin runs as is a good choice. Windows' service manager keeps its password; setup checks
   it and doesn't store it. A mapped drive (`Z:`) becomes its share path for the service (`PATH_MAP`).
5. **Name and port** (default `Open Video Menu on <PC>`, 8790), and optional extra folders.

Then it writes the settings, installs and starts the service, opens the firewall on home (private) networks only
(TCP 8790 and UDP 5353 for discovery), and checks the server answers.

| What | Where |
|---|---|
| Program | `C:\Program Files\Open Video Menu\Menu Server` (`node.exe`, `menu-server.mjs`, WinSW as `OpenVideoMenuServer.exe`) |
| Settings | `%ProgramData%\Open Video Menu\menu-server.env`, readable only by Windows, administrators and the service. It holds the Plex token or Jellyfin API key. |
| Logs | `%ProgramData%\Open Video Menu\logs` |
| Service | "Open Video Menu menu server" (`OpenVideoMenuServer`): automatic (delayed) start, restarts on failure |
| Status | http://127.0.0.1:8790/v1/health (Start menu: Open Video Menu > Menu server status) |

Running the installer again changes the settings (it replaces the service). Uninstalling removes the service, the
firewall rules and the program, and asks whether to remove the settings and logs.

### Silent install

```
OpenVideoMenu-MenuServer-Setup.exe /VERYSILENT /PLEXURL=http://127.0.0.1:32400 /PLEXTOKEN=<token>
OpenVideoMenu-MenuServer-Setup.exe /VERYSILENT /JELLYFINURL=http://127.0.0.1:8096 /JELLYFINKEY=<api key>
```

Optional: `/ACCOUNT=PC\user /PASSWORD=…` (network shares), `/SERVERNAME=…`, `/PORT=…`, `/MEDIAROOTS=D:\More`,
`/PATHMAP=Z:\=\\nas\media\`. (A password on the command line is visible to other programs on the PC while setup runs.)

## Building it

```
pwsh packaging/windows/build.ps1 -Version 0.1.0
```

It needs Node.js, the repository's npm packages (`npm ci`) and Inno Setup 6 or 7. It bundles the server
(`packaging/bundle.mjs`), downloads Node.js for Windows (checked against nodejs.org's `SHASUMS256.txt`) and WinSW
2.12.0 (checked against a pinned hash) into `packaging/build/cache`, and writes
`packaging/build/out/OpenVideoMenu-MenuServer-Setup-<version>.exe` and `OpenVideoMenu-MenuServer-Setup.exe`.

The files: `menu-server.iss` (the installer: wizard pages and steps), `setup-helper.ps1` (talks to Plex and Jellyfin,
writes the settings, installs the service and the firewall rules), `OpenVideoMenuServer.template.xml` (the service),
`install-readme.txt` (installed as README.txt).

### Signing

The installer is unsigned for now, so Windows SmartScreen shows "Windows protected your PC" (More info > Run
anyway). To sign, set `OVM_SIGN_CMD` to a signing command with `$f` for the file; Inno Setup signs the setup and
its uninstaller:

```
# A code-signing certificate, with signtool from the Windows SDK:
$env:OVM_SIGN_CMD = 'signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /a $f'
# Azure Trusted Signing (signtool with its dlib):
$env:OVM_SIGN_CMD = 'signtool sign /fd sha256 /tr http://timestamp.acs.microsoft.com /td sha256 /dlib Azure.CodeSigning.Dlib.dll /dmdf metadata.json $f'
```
