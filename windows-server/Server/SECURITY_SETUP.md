# Security Setup Guide

## Overview
The PC Remote Server uses **SSH key-based authentication** for secure access.

## Certificate
The server automatically generates a self-signed certificate (`server.pfx`) on first run. This is used for TLS encryption.

**Note**: For production use, replace this with a proper certificate from a Certificate Authority.

## Authentication Setup

### Key-Based Authentication (Required)

1. **Get your public key from the Android app:**
   - The app generates an EC (Elliptic Curve) key pair automatically
   - The public key is stored in the app's encrypted preferences
   - Tap the copy icon next to "SSH Key Ready" to copy your public key

2. **Add the public key to `authorized_keys.txt`:**
   - Create `authorized_keys.txt` in the Server directory
   - Add one public key per line (base64 encoded)
   - Example:
     ```
     MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...
     ```

3. **In your Android app**, connect using the server address and port. The app will automatically use key-based authentication.

## File Locations
All security files should be in the `windows-server/Server` directory:
- `server.pfx` - Server certificate (auto-generated)
- `authorized_keys.txt` - Public keys (required for authentication)

## Security Best Practices
1. Store `authorized_keys.txt` securely
2. Don't commit security files to version control
3. For production, use a proper SSL certificate
4. Consider using a VPN (like Tailscale) for additional security
5. Enable Windows Firewall rules for port 8888
6. Only add trusted public keys to `authorized_keys.txt`

## Troubleshooting
- **Certificate issues**: Delete `server.pfx` and restart server to regenerate
- **Authentication fails**: Check that keys are correctly formatted (base64) and match the public key from the app
- **Connection refused**: Check firewall settings for port 8888
- **Key not found**: Ensure `authorized_keys.txt` exists and contains the correct public key from your Android app