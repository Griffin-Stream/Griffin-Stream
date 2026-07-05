using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PCRemote.Server.WOL;

public static class WOLHelper
{
    public static bool SendMagicPacket(string macAddress)
    {
        try
        {
            var macBytes = ParseMacAddress(macAddress);
            var packet = CreateMagicPacket(macBytes);
            
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            
            // Send to all broadcast addresses on all network interfaces
            var broadcastAddresses = GetBroadcastAddresses();
            
            if (broadcastAddresses.Count == 0)
            {
                // Fallback to 255.255.255.255
                var endPoint = new IPEndPoint(IPAddress.Broadcast, 9);
                socket.SendTo(packet, endPoint);
                Console.WriteLine($"[WOL] Sent magic packet to {macAddress} via 255.255.255.255");
            }
            else
            {
                foreach (var broadcastAddress in broadcastAddresses)
                {
                    var endPoint = new IPEndPoint(broadcastAddress, 9);
                    socket.SendTo(packet, endPoint);
                    Console.WriteLine($"[WOL] Sent magic packet to {macAddress} via {broadcastAddress}");
                }
            }
            
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WOL] Error sending magic packet: {ex.Message}");
            return false;
        }
    }
    
    private static byte[] ParseMacAddress(string macAddress)
    {
        var cleanMac = macAddress.Replace(":", "").Replace("-", "").Replace(" ", "");
        if (cleanMac.Length != 12)
        {
            throw new ArgumentException("Invalid MAC address format");
        }
        
        var bytes = new byte[6];
        for (int i = 0; i < 6; i++)
        {
            var hex = cleanMac.Substring(i * 2, 2);
            bytes[i] = Convert.ToByte(hex, 16);
        }
        return bytes;
    }
    
    private static byte[] CreateMagicPacket(byte[] macAddress)
    {
        var packet = new byte[102];
        
        // First 6 bytes: 0xFF
        for (int i = 0; i < 6; i++)
        {
            packet[i] = 0xFF;
        }
        
        // Followed by 16 repetitions of the MAC address
        for (int i = 0; i < 16; i++)
        {
            Array.Copy(macAddress, 0, packet, 6 + i * 6, 6);
        }
        
        return packet;
    }
    
    private static List<IPAddress> GetBroadcastAddresses()
    {
        var broadcastAddresses = new List<IPAddress>();
        
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            // Only get active interfaces
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
                continue;
            
            // Skip loopback and non-IP interfaces
            if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            
            var ipProperties = networkInterface.GetIPProperties();
            foreach (var unicastAddress in ipProperties.UnicastAddresses)
            {
                if (unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    var ip = unicastAddress.Address;
                    var mask = unicastAddress.IPv4Mask;
                    
                    if (mask != null)
                    {
                        // Calculate broadcast address: (IP & mask) | ~mask
                        var ipBytes = ip.GetAddressBytes();
                        var maskBytes = mask.GetAddressBytes();
                        var broadcastBytes = new byte[4];
                        
                        for (int i = 0; i < 4; i++)
                        {
                            broadcastBytes[i] = (byte)((ipBytes[i] & maskBytes[i]) | ~maskBytes[i]);
                        }
                        
                        var broadcastAddress = new IPAddress(broadcastBytes);
                        if (!broadcastAddresses.Contains(broadcastAddress))
                        {
                            broadcastAddresses.Add(broadcastAddress);
                        }
                    }
                }
            }
        }
        
        return broadcastAddresses;
    }
}
