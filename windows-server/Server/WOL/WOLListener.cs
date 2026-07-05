using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PCRemote.Server.WOL;

public class WOLListener
{
    private UdpClient? _udpClient;
    private bool _listening = false;

    public void StartListening()
    {
        if (_listening) return;

        try
        {
            _udpClient = new UdpClient(9); // WOL port
            _listening = true;

            _ = Task.Run(async () =>
            {
                while (_listening)
                {
                    try
                    {
                        var result = await _udpClient.ReceiveAsync();
                        var data = result.Buffer;

                        // Check if it's a magic packet
                        if (IsMagicPacket(data, GetLocalMacAddress()))
                        {
                            Console.WriteLine("Received Wake-on-LAN magic packet");
                            // PC should wake up (if configured in BIOS)
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error receiving WOL packet: {ex.Message}");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to start WOL listener: {ex.Message}");
        }
    }

    public void StopListening()
    {
        _listening = false;
        _udpClient?.Close();
        _udpClient?.Dispose();
    }

    private bool IsMagicPacket(byte[] data, PhysicalAddress macAddress)
    {
        if (data.Length < 102) return false;

        // Check first 6 bytes are 0xFF
        for (int i = 0; i < 6; i++)
        {
            if (data[i] != 0xFF) return false;
        }

        // Check for 16 repetitions of MAC address
        var macBytes = macAddress.GetAddressBytes();
        for (int i = 0; i < 16; i++)
        {
            for (int j = 0; j < 6; j++)
            {
                if (data[6 + i * 6 + j] != macBytes[j]) return false;
            }
        }

        return true;
    }

    private PhysicalAddress GetLocalMacAddress()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                networkInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            {
                if (networkInterface.OperationalStatus == OperationalStatus.Up)
                {
                    return networkInterface.GetPhysicalAddress();
                }
            }
        }
        return PhysicalAddress.None;
    }
}
