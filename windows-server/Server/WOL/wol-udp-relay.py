#!/usr/bin/env python3
"""
Wake-on-LAN UDP Relay
Listens for WOL magic packets on UDP port 9 and rebroadcasts them to the local network.
This allows sending WOL packets to a specific IP (e.g. Tailscale IP) which are then 
relayed as broadcasts on the LAN.

Usage:
    sudo python3 wol-udp-relay.py [--port 9]
"""

import socket
import argparse
import sys
import time
import hashlib

def is_magic_packet(data):
    """
    Check if data appears to be a magic packet.
    Format: 6 bytes of 0xFF, followed by 16 repetitions of the 6-byte MAC.
    Total size: 6 + 16*6 = 102 bytes.
    """
    if len(data) != 102:
        return False
    
    # Check first 6 bytes
    if data[:6] != b'\xff' * 6:
        return False
        
    return True

def _local_ipv4_addresses():
    """Best-effort list of this machine's IPv4 addresses (stdlib only)."""
    ips = set()
    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            ips.add(info[4][0])
    except Exception:
        pass
    # Also grab the primary outbound interface address.
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(('8.8.8.8', 80))
        ips.add(s.getsockname()[0])
        s.close()
    except Exception:
        pass
    return ips

def _detect_tailscale_ip(addresses):
    """Tailscale hands out addresses in the 100.64.0.0/10 CGNAT range."""
    return next((ip for ip in addresses if ip.startswith('100.')), None)

def _detect_lan_broadcast(addresses):
    """Directed broadcast for the primary private LAN interface (skips Tailscale/loopback/APIPA)."""
    for ip in addresses:
        if ip.startswith('127.') or ip.startswith('169.254.') or ip.startswith('100.'):
            continue
        is_private = (
            ip.startswith('10.')
            or ip.startswith('192.168.')
            or any(ip.startswith(f'172.{n}.') for n in range(16, 32))
        )
        if is_private:
            octets = ip.split('.')
            if len(octets) == 4:
                return f"{octets[0]}.{octets[1]}.{octets[2]}.255"
    return None

def run_relay(port=9, bind_ip=None, broadcast_ip=None):
    # Create listening socket (UDP)
    try:
        listener = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        listener.bind(('0.0.0.0', port))
        print(f"[WOL Relay] Listening on UDP port {port}...")
    except Exception as e:
        print(f"[Error] Failed to bind to port {port}: {e}")
        sys.exit(1)

    # Resolve the source (Tailscale) interface and the LAN broadcast target. These are
    # auto-detected from this machine's own interfaces so nothing is hardcoded; override
    # with --bind / --broadcast if detection picks the wrong interface.
    addresses = _local_ipv4_addresses()
    if bind_ip is None:
        bind_ip = _detect_tailscale_ip(addresses) or '0.0.0.0'
    if broadcast_ip is None:
        broadcast_ip = _detect_lan_broadcast(addresses) or '255.255.255.255'

    # Create broadcast socket - bind to specific source to avoid loop
    broadcaster = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    broadcaster.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    # Bind broadcaster to the Tailscale interface to prevent it from receiving LAN broadcasts
    try:
        broadcaster.bind((bind_ip, 0))
    except Exception as e:
        print(f"[Warn] Could not bind broadcaster to {bind_ip} ({e}); falling back to 0.0.0.0")
        bind_ip = '0.0.0.0'
        broadcaster.bind((bind_ip, 0))

    BROADCAST_IP = broadcast_ip
    print(f"[WOL Relay] Source interface: {bind_ip}  ->  LAN broadcast: {BROADCAST_IP}")

    print("[WOL Relay] Ready to relay packets from Tailscale to LAN")
    packet_count = 0
    seen_packets = {}

    while True:
        try:
            data, addr = listener.recvfrom(1024)
            sender_ip = addr[0]
            
            # Only accept packets from Tailscale network (100.x.x.x)
            if not sender_ip.startswith('100.'):
                continue

            # Deduplication
            pkt_hash = hashlib.md5(data).hexdigest()
            now = time.time()
            
            # Prune old packets (older than 2 seconds)
            seen_packets = {k: v for k, v in seen_packets.items() if now - v < 2.0}
            
            if pkt_hash in seen_packets:
                print(f"[WOL Relay] Ignoring duplicate packet from {sender_ip} (hash: {pkt_hash[:8]})")
                continue
                
            seen_packets[pkt_hash] = now

            if is_magic_packet(data):
                packet_count += 1
                mac_bytes = data[6:12]
                mac_str = ':'.join(format(b, '02x') for b in mac_bytes).upper()
                print(f"[WOL Relay #{packet_count}] Received from {sender_ip} for MAC: {mac_str}. Broadcasting to {BROADCAST_IP}")
                broadcaster.sendto(data, (BROADCAST_IP, 9))
            else:
                print(f"[WOL Relay] Received non-WOL packet of length {len(data)} from {sender_ip}")
                
        except KeyboardInterrupt:
            print(f"\n[WOL Relay] Stopping... (relayed {packet_count} packets)")
            break
        except Exception as e:
            print(f"[Error] {e}")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description='WOL UDP Relay')
    parser.add_argument('--port', type=int, default=9, help='UDP port to listen on (default: 9)')
    parser.add_argument('--bind', default=None,
                        help='Source IP to send broadcasts from (default: auto-detect Tailscale IP)')
    parser.add_argument('--broadcast', default=None,
                        help='LAN broadcast address, e.g. 192.168.1.255 (default: auto-detect)')
    args = parser.parse_args()
    
    run_relay(args.port, args.bind, args.broadcast)
