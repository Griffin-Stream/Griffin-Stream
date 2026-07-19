Griffin Stream Server
=====================

This is the Windows server for the Griffin Stream Android app. It streams your desktop
and accepts mouse/keyboard/gamepad input from the app over an encrypted (TLS) connection.

QUICK START (SAME NETWORK)
--------------------------
1. Run "Server.exe" (or the Start Menu shortcut "Griffin Stream Server").
2. On first run the server generates a TLS certificate (server.pfx) automatically.
3. Note the local IP and pairing PIN shown in the server window.
4. On your phone/tablet (same Wi-Fi / LAN as this PC), open Griffin Stream, enter the
   PIN when prompted, and connect using this PC's local IP on port 8888.

Do not expose port 8888 to the public internet.

GAMEPAD SUPPORT (OPTIONAL)
--------------------------
Virtual gamepad input requires the ViGEmBus driver, which is NOT bundled with this
installer (it is a kernel driver and must be installed separately):
  https://github.com/nefarius/ViGEmBus/releases
Mouse and keyboard work without it.

FIREWALL
--------
To accept connections, Windows Firewall must allow inbound TCP port 8888. The installer
can add this rule for you (optional, requires administrator approval), or Windows may
prompt you to allow the app the first time it accepts a connection.

DEBUG LOG
---------
The server runs as a windowed app - no console window by default. Click "Show debug log" in
the server window to attach a live terminal (useful for troubleshooting); click it again to
hide it. A rolling log is also written to %LOCALAPPDATA%\GriffinStream\logs regardless.

UPDATES
-------
Click "Check for updates" in the server window. When a newer build is available, the server
downloads it and installs silently in the background, then relaunches.

WHERE THINGS ARE
----------------
This app installs per-user under %LOCALAPPDATA%\GriffinStream so it can save its runtime
files (server.pfx, authorized_keys.txt, password.hash) next to the executable without
requiring administrator rights. Your Pro license (if any) is cached under
%APPDATA%\GriffinStream.

UNINSTALL
---------
Use "Add or remove programs" or the Start Menu uninstall shortcut. The uninstaller removes the
app and its logs, and asks whether to also delete your paired devices and Pro license (choose
No to keep them for a reinstall). The ViGEmBus gamepad driver, if you installed it, is a shared
system driver and is left in place.
