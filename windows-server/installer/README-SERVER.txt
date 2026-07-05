Griffin Stream Server
=====================

This is the Windows server for the Griffin Stream Android app. It streams your desktop
and accepts mouse/keyboard/gamepad input from the app over an encrypted (TLS) connection.

QUICK START
-----------
1. Run "Server.exe" (or the Start Menu shortcut "Griffin Stream Server").
2. On first run the server generates a TLS certificate (server.pfx) automatically.
3. In the Android app, copy your public key and add it to authorized_keys.txt in this
   folder (see SECURITY_SETUP for details), then connect to this PC's IP on port 8888.

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

WHERE THINGS ARE
----------------
This app installs per-user under %LOCALAPPDATA%\GriffinStream so it can save its runtime
files (server.pfx, authorized_keys.txt, password.hash) next to the executable without
requiring administrator rights.

UNINSTALL
---------
Use "Add or remove programs" or the Start Menu uninstall shortcut.
