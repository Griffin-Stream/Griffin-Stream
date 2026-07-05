# Wake-on-LAN Solutions (When PC is Completely Off)

Since the server runs on the target PC, it won't be available when the PC is off. Here are practical solutions:

## Option 1: Tailscale Subnet Router & UDP Relay (Best Solution)

If you have **another always-on device** (Raspberry Pi, NAS, another PC, etc.) on your network:

1. **Install Tailscale on the always-on device**
2. **Enable Subnet Routing in Tailscale:**
   - In Tailscale admin console: https://login.tailscale.com/admin/machines
   - Find your always-on device
   - Enable "Subnet Routes" for your local network
   - Approve the route

3. **Run the UDP WOL relay** on the always-on device (see Option 3 below)

4. **In the Android app:**
   - Enter your PC's MAC address
   - Enter the always-on device's Tailscale IP in "WOL Relay IP" field
   - The app will send WOL requests to the relay device

**Advantages:**
- ✅ Works when PC is completely off
- ✅ Uses existing Tailscale setup
- ✅ No router access needed

---

## Option 2: Keep PC in Sleep Mode (Simplest)

Instead of shutting down completely, use **Sleep** or **Hibernate**:

- **Sleep**: PC uses minimal power, wakes quickly, network may stay active
- **Hibernate**: PC uses no power, wakes slower, WOL works

**How to enable:**
- Windows Settings → System → Power & Sleep
- Set "When plugged in, PC goes to sleep after: [time]"
- Enable "Wake on LAN" in network adapter settings:
  - Device Manager → Network Adapters → Your adapter → Properties → Power Management → Enable "Wake on Magic Packet"

**Advantages:**
- ✅ No additional hardware needed
- ✅ WOL works reliably
- ✅ Server can still be reachable (if network stays active)

---

## Option 3: UDP WOL Relay (For Always-On Device)

A Python script is provided: `wol-udp-relay.py`

### Relay Mode:
Listens for WOL requests on UDP port 9 and relays them as local broadcast:

1. **Copy `wol-udp-relay.py` to your always-on device**
2. **Run the relay:**
   ```bash
   sudo python3 wol-udp-relay.py --port 9
   ```
   (Use `nohup` or a systemd service to keep it running)

3. **In the Android app:**
   - Enter your PC's MAC address
   - Enter the always-on device's Tailscale IP in "WOL Relay IP" field
   - The app will send WOL packets to port 9 on the relay device

**Note:** The Android app sends UDP packets. This relay simply listens for them on the Tailscale IP and rebroadcasts them to the local LAN.

---

## Option 4: Cloud-Based WOL Service (Advanced)

Use a service like:
- **Home Assistant** with WOL integration
- **Homebridge** with WOL plugin
- **Custom cloud service** that stores WOL requests and relays them

**Advantages:**
- ✅ Works from anywhere
- ✅ No always-on device needed (if using cloud service)

**Disadvantages:**
- ⚠️ Requires additional setup
- ⚠️ May require subscription for cloud services

---

## Recommended Approach

**For your situation (no router access, using Tailscale):**

1. **Short term**: Use Sleep mode instead of shutdown
2. **Long term**: Set up Tailscale subnet routing with an always-on device (Raspberry Pi is perfect - $35, low power)

The subnet router approach is the most reliable for completely off PCs.

---

## How the Android App Works

The app tries WOL in this order:
1. **Via server connection** (if connected) - works when PC is on/sleeping
2. **Via WOL Relay IP** (if configured) - works when PC is off, requires always-on device
3. **Local broadcast** (fallback) - only works on same network
