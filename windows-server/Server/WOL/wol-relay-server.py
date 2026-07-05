#!/usr/bin/env python3
"""
Wake-on-LAN Relay Server
Listens for WOL requests on a TCP port and relays them to the local network.
Useful when running on an always-on device (Raspberry Pi, etc.) on Tailscale.

Usage:
    python3 wol-relay-server.py [--port PORT] [--mac MAC_ADDRESS]

Example:
    # Listen on port 8889, wake PC with specific MAC
    python3 wol-relay-server.py --port 8889 --mac AA:BB:CC:DD:EE:FF
    
    # Or let clients specify MAC address in request
    python3 wol-relay-server.py --port 8889
"""

import socket
import sys
import argparse
import threading

def send_wol(mac_address, port=9):
    """Send Wake-on-LAN magic packet"""
    try:
        mac_clean = mac_address.replace(':', '').replace('-', '').replace(' ', '')
        if len(mac_clean) != 12:
            raise ValueError("Invalid MAC address format")
        
        mac_bytes = bytes.fromhex(mac_clean)
        packet = b'\xff' * 6 + mac_bytes * 16
        
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        sock.sendto(packet, ('255.255.255.255', port))
        sock.close()
        
        print(f"[WOL] Sent magic packet for {mac_address}")
        return True
    except Exception as e:
        print(f"[WOL] Error: {e}")
        return False

def handle_client(client_socket, default_mac=None):
    """Handle client connection"""
    try:
        # Read MAC address from client (simple protocol: just send MAC as text)
        data = client_socket.recv(1024).decode('utf-8').strip()
        mac_address = data if data else default_mac
        
        if not mac_address:
            client_socket.send(b"ERROR: No MAC address provided\n")
            return
        
        # Send WOL packet
        success = send_wol(mac_address)
        
        if success:
            client_socket.send(b"OK: WOL packet sent\n")
        else:
            client_socket.send(b"ERROR: Failed to send WOL packet\n")
    except Exception as e:
        print(f"[Error] Handling client: {e}")
        try:
            client_socket.send(f"ERROR: {e}\n".encode())
        except:
            pass
    finally:
        client_socket.close()

def main():
    parser = argparse.ArgumentParser(description='WOL Relay Server')
    parser.add_argument('--port', type=int, default=8889, help='Port to listen on (default: 8889)')
    parser.add_argument('--mac', type=str, default=None, help='Default MAC address to wake')
    args = parser.parse_args()
    
    server_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server_socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server_socket.bind(('0.0.0.0', args.port))
    server_socket.listen(5)
    
    print(f"[WOL Relay] Server listening on port {args.port}")
    if args.mac:
        print(f"[WOL Relay] Default MAC: {args.mac}")
    print("[WOL Relay] Ready to receive WOL requests...")
    
    while True:
        try:
            client_socket, address = server_socket.accept()
            print(f"[WOL Relay] Connection from {address[0]}:{address[1]}")
            thread = threading.Thread(target=handle_client, args=(client_socket, args.mac))
            thread.daemon = True
            thread.start()
        except KeyboardInterrupt:
            print("\n[WOL Relay] Shutting down...")
            break
        except Exception as e:
            print(f"[WOL Relay] Error: {e}")
    
    server_socket.close()

if __name__ == '__main__':
    main()
