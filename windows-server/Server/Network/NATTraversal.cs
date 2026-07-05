using System.Net;
using System.Net.Sockets;

namespace PCRemote.Server.Network;

public class NATTraversal
{
    public static async Task<IPEndPoint?> GetPublicEndpoint(string stunServer = "stun.l.google.com", int stunPort = 19302)
    {
        try
        {
            using var udpClient = new UdpClient();
            udpClient.Client.ReceiveTimeout = 5000;

            var stunAddress = Dns.GetHostAddresses(stunServer)[0];
            var request = CreateSTUNBindingRequest();

            await udpClient.SendAsync(request, request.Length, new IPEndPoint(stunAddress, stunPort));
            var result = await udpClient.ReceiveAsync();

            return ParseSTUNResponse(result.Buffer);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"STUN request failed: {ex.Message}");
            return null;
        }
    }

    private static byte[] CreateSTUNBindingRequest()
    {
        var buffer = new byte[20];
        buffer[0] = 0x00;
        buffer[1] = 0x01; // Binding request
        buffer[2] = 0x00;
        buffer[3] = 0x00; // Message length
        buffer[4] = 0x21;
        buffer[5] = 0x12;
        buffer[6] = 0xA4;
        buffer[7] = 0x42; // Magic cookie
        // Transaction ID (simplified - would be random)
        return buffer;
    }

    private static IPEndPoint? ParseSTUNResponse(byte[] data)
    {
        if (data.Length < 20) return null;

        // Check magic cookie
        if (data[4] != 0x21 || data[5] != 0x12 || data[6] != 0xA4 || data[7] != 0x42)
        {
            return null;
        }

        // Parse attributes
        int offset = 20;
        while (offset < data.Length - 4)
        {
            ushort attributeType = (ushort)((data[offset] << 8) | data[offset + 1]);
            ushort attributeLength = (ushort)((data[offset + 2] << 8) | data[offset + 3]);
            offset += 4;

            if (attributeType == 0x0001) // MAPPED-ADDRESS
            {
                if (offset + 8 > data.Length) break;
                byte family = data[offset + 1];
                ushort port = (ushort)((data[offset + 2] << 8) | data[offset + 3]);
                var addressBytes = new byte[4];
                Array.Copy(data, offset + 4, addressBytes, 0, 4);
                var address = new IPAddress(addressBytes);
                return new IPEndPoint(address, port);
            }
            else if (attributeType == 0x0020) // XOR-MAPPED-ADDRESS
            {
                if (offset + 8 > data.Length) break;
                byte family = data[offset + 1];
                ushort xPort = (ushort)((data[offset + 2] << 8) | data[offset + 3]);
                ushort port = (ushort)(xPort ^ 0x2112);
                var addressBytes = new byte[4];
                Array.Copy(data, offset + 4, addressBytes, 0, 4);
                // XOR decode
                for (int i = 0; i < 4; i++)
                {
                    addressBytes[i] ^= data[4 + i];
                }
                var address = new IPAddress(addressBytes);
                return new IPEndPoint(address, port);
            }
            else
            {
                offset += attributeLength;
            }
        }

        return null;
    }
}
