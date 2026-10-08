Open Video Menu menu server
https://openvideomenu.org/

The menu server hands Open Video Menu menus and their art to TV apps (ovmplayer) on your home network. It reads
the library folders of your Plex or Jellyfin server and follows them as they change. It only reads: it never
writes to your media and never serves your video.

It runs as the Windows service "Open Video Menu menu server" and starts with Windows.

  Settings:  %ProgramData%\Open Video Menu\menu-server.env  (holds your media server sign-in; only Windows and
             administrators can read it). Run the installer again to change them.
  Logs:      %ProgramData%\Open Video Menu\logs
  Status:    http://127.0.0.1:8790/v1/health  (Start menu: Open Video Menu > Menu server status)

To remove it: Settings > Apps > Open Video Menu menu server > Uninstall.
