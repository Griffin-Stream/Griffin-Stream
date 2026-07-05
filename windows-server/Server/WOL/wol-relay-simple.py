#!/usr/bin/env python3
"""
Simple Wake-on-LAN Relay Service
Run this on an always-on device (Raspberry Pi, NAS, another PC, etc.)
that's on Tailscale to relay WOL packets when the target PC is off.

Usage:
    python3 wol-relay-simple.py <MAC_ADDRESS> [target_ip]

Examples:
    # Send WOL to MAC address (broadcast on local network)
    python3 wol-relay-simple.py AA:BB:CC:DD:EE:FF
    
    # Send WOL to specific IP (e.g., Tailscale IP)
    python3 wol-relay-simple.py AA:BB:CC:DD:EE:FF 100.64.1.2
"""

import socket
import sys

def send_wol(mac_address, target_ip=None, port=9):
    """Send Wake-on-LAN magic packet"""
    try:
        # Parse MAC address
        mac_clean = mac_address.replace(':', '').replace('-', '').replace(' ', '')
        if len(mac_clean) != 12:
            raise ValueError("Invalid MAC address format")
        
        mac_bytes = bytes.fromhex(mac_clean)
        
        # Create magic packet: 6 bytes of 0xFF + 16 repetitions of MAC address
        packet = b'\xff' * 6 + mac_bytes * 16
        
        # Create socket
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        
        if target_ip:
            # Send to specific IP
            sock.sendto(packet, (target_ip, port))
            print(f"✓ Sent WOL packet for {mac_address} to {target_ip}:{port}")
        else:
            # Broadcast to all interfaces
            sock.sendto(packet, ('255.255.255.255', port))
            print(f"✓ Sent WOL packet for {mac_address} via broadcast")
        
        sock.close()
        return True
    except Exception as e:
        print(f"✗ Error sending WOL packet: {e}")
        return False

if __name__ == '__main__':
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    
    mac_address = sys.argv[1]
    target_ip = sys.argv[2] if len(sys.argv) > 2 else None
    
    success = send_wol(mac_address, target_ip)
    sys.exit(0 if success else 1)
