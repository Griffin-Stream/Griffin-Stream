# Remote Access Setup (No Router Access Required)

Since you don't have access to your router, use one of these solutions to connect from outside your network:

## Option 1: Tailscale (Recommended - Easiest)

**Best for**: Permanent setup, low latency, secure

1. **Install Tailscale on Windows Server:**
   - Download from: https://tailscale.com/download
   - Install and sign in with your account

2. **Install Tailscale on Android:**
   - Install from Google Play Store
   - Sign in with the same account

3. **Connect:**
   - Open Tailscale on Windows - note the IP address (e.g., `100.x.x.x`)
   - In the Android app, connect using that Tailscale IP and port `8888`
   - Example: `100.64.1.2:8888`

**Advantages:**
- ✅ Free for personal use
- ✅ Works automatically through NAT
- ✅ Secure (WireGuard-based)
- ✅ Low latency (direct connection when possible)
- ✅ No router configuration needed

---

## Option 2: ngrok (Quick Setup)

**Best for**: Quick testing, temporary access

1. **Sign up and install:**
   - Sign up at https://ngrok.com (free tier available)
   - Download ngrok for Windows
   - Get your authtoken from the dashboard

2. **Run ngrok:**
   ```bash
   ngrok config add-authtoken YOUR_TOKEN
   ngrok tcp 8888
   ```

3. **Connect:**
   - ngrok will show an address like: `0.tcp.ngrok.io:12345`
   - In the Android app, connect using that address
   - Note: Free tier gives you a new address each time you restart

**Advantages:**
- ✅ Very quick to set up
- ✅ Free tier available
- ✅ No router access needed

**Disadvantages:**
- ⚠️ Free tier has connection limits
- ⚠️ Address changes on restart (unless paid plan)

---

## Option 3: Cloudflare Tunnel (Free, Unlimited)

**Best for**: Free permanent solution

1. **Install cloudflared:**
   - Download from: https://developers.cloudflare.com/cloudflare-one/connections/connect-apps/install-and-setup/
   - Extract to a folder

2. **Run tunnel:**
   ```bash
   cloudflared tunnel --url tcp://localhost:8888
   ```

3. **Connect:**
   - Use the address shown (e.g., `something.trycloudflare.com:port`)
   - In the Android app, connect using that address

**Advantages:**
- ✅ Completely free
- ✅ No account limits
- ✅ Works through any NAT

**Disadvantages:**
- ⚠️ Address changes on restart
- ⚠️ Requires cloudflared to be running

---

## Option 4: ZeroTier (Similar to Tailscale)

**Best for**: Custom network control

1. **Create network:**
   - Sign up at https://my.zerotier.com
   - Create a new network
   - Note the Network ID

2. **Install on Windows:**
   - Download ZeroTier for Windows
   - Join your network using the Network ID

3. **Install on Android:**
   - Install ZeroTier from Play Store
   - Join the same network

4. **Connect:**
   - Find your ZeroTier IP in the ZeroTier app
   - Connect using that IP and port `8888`

**Advantages:**
- ✅ Free for personal use
- ✅ Works through NAT
- ✅ More control over network settings

---

## Quick Comparison

| Solution | Setup Time | Cost | Address Stability | Best For |
|----------|------------|------|-------------------|----------|
| **Tailscale** | 5 min | Free | Permanent | Daily use |
| **ngrok** | 2 min | Free/Paid | Changes | Testing |
| **Cloudflare** | 3 min | Free | Changes | Free permanent |
| **ZeroTier** | 5 min | Free | Permanent | Custom networks |

---

## Recommendation

**For your use case (remote desktop with video streaming):**

👉 **Use Tailscale** - It's the easiest, most reliable, and gives you the best performance for remote desktop use. The setup takes 5 minutes and you'll have a permanent solution.

---

## Troubleshooting

- **Can't connect through tunnel?**
  - Make sure the tunnel service is running
  - Check that the server is listening on port 8888
  - Verify firewall allows the connection

- **High latency?**
  - Tailscale/ZeroTier usually have the lowest latency
  - ngrok/Cloudflare may route through their servers (adds latency)

- **Connection drops?**
  - Ensure the tunnel service stays running
  - Some services (ngrok free tier) have connection limits
