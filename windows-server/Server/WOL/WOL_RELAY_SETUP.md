# Wake-on-LAN Solutions (When PC is Completely Off)

Since the server runs on the target PC, it won't be available when the PC is off. Practical options:

## Option 1: Keep PC in Sleep Mode (Simplest)

Instead of shutting down completely, use **Sleep** or **Hibernate**:

- **Sleep**: PC uses minimal power, wakes quickly, network may stay active
- **Hibernate**: PC uses no power, wakes slower, WOL works

**How to enable:**
- Windows Settings → System → Power & Sleep
- Set "When plugged in, PC goes to sleep after: [time]"
- Enable "Wake on LAN" in network adapter settings:
  - Device Manager → Network Adapters → Your adapter → Properties → Power Management → Enable "Wake on Magic Packet"

**Advantages:**
- No additional hardware needed
- WOL works reliably on the LAN
- Server can still be reachable (if network stays active)

---

## Option 2: UDP WOL Relay (Always-On Device on the Same LAN)

If you have **another always-on device** (Raspberry Pi, NAS, another PC) on the **same local network**:

A Python script is provided: `wol-udp-relay.py`

### Relay Mode
Listens for WOL requests on UDP port 9 and relays them as local broadcast:

1. **Copy `wol-udp-relay.py` to your always-on device**
2. **Run the relay:**
   ```bash
   sudo python3 wol-udp-relay.py --port 9
   ```
   (Use `nohup` or a systemd service to keep it running)

3. **In the Android app:**
   - Enter your PC's MAC address
   - Enter the always-on device's **local LAN IP** in "WOL Relay IP"
   - The app will send WOL packets to port 9 on the relay device

**Note:** Prefer Sleep mode when possible. Do not expose WOL relays or port 8888 to the public internet.

---

## Recommended Approach

1. **Default**: Use Sleep instead of full shutdown
2. **If you need wake-from-off**: run the UDP relay on another always-on device on the same LAN
