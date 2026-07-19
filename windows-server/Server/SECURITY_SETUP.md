# Security Setup Guide

## Overview

Griffin Stream protects the link with **TLS 1.3** and enrolls phones via a **pairing PIN** plus a
per-device ECDSA identity (generated automatically in the Android app). There is no SSH protocol
and no manual “copy public key into a text file” step.

## Certificate

The server automatically generates a self-signed certificate (`server.pfx`) on first run for TLS.

**Note:** For production deployments you may replace this with a certificate from a Certificate Authority.

## Pairing (required once per phone)

1. Start **Griffin Stream Server** on the PC — the dashboard shows a **pairing PIN** and local IP.
2. On the phone (same LAN), enter the PC’s IP (port `8888` by default) and tap **Connect**.
3. When prompted, type the PIN from the server window.
4. The phone’s device identity is enrolled on the PC. Later connects prove that identity — no PIN
   unless you reset the phone’s identity or wipe paired devices on the PC.

## File locations (install folder)

Typical path: `%LOCALAPPDATA%\GriffinStream`

| File | Purpose |
|------|---------|
| `server.pfx` | TLS certificate (auto-generated) |
| `server.pfx.dpapi` | DPAPI-protected PFX password |
| `authorized_keys.json` | Enrolled device identities (created/updated by PIN pairing) |

The legacy line-based `authorized_keys.txt` is **not used**.

## Security best practices

1. Prefer same-LAN pairing; do **not** expose TCP port 8888 to the public internet.
2. Don’t commit security files (`server.pfx`, `authorized_keys.json`, license data) to version control.
3. Enable Windows Firewall rules for port 8888 (installer can add this optionally).
4. Revoke phones you no longer trust from the server’s paired-devices list (or by wiping
   `authorized_keys.json` and re-pairing).
5. On the phone, **Advanced options → Reset device identity** only if you need a fresh identity
   (you will pair again with the PIN).

## Troubleshooting

- **Certificate / “identity changed”:** Server was reinstalled or `server.pfx` was regenerated —
  enter the pairing PIN when the app prompts.
- **Authentication failed:** Reconnect and enter the current PIN from the server window (PIN
  refreshes when the server restarts until the device is enrolled).
- **Connection refused:** Confirm the server is running and firewall allows port 8888.
