using System.Net;
using System.Net.NetworkInformation;

namespace PCRemote.Server.Network;

public class PortForwardingHelper
{
    public static async Task<IPAddress?> GetPublicIPAsync()
    {
        // Try STUN first
        try
        {
            var endpoint = await NATTraversal.GetPublicEndpoint();
            if (endpoint != null)
            {
                return endpoint.Address;
            }
        }
        catch
        {
            // STUN failed, try fallback
        }

        // Fallback: Try to get public IP from a web service
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = await client.GetStringAsync("https://api.ipify.org");
            if (IPAddress.TryParse(response.Trim(), out var ip))
            {
                return ip;
            }
        }
        catch
        {
            // Fallback failed
        }

        return null;
    }

    public static IPAddress? GetLocalIPAddress()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                           ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(ni => ni.Speed);

            foreach (var ni in interfaces)
            {
                var props = ni.GetIPProperties();
                var ipv4 = props.UnicastAddresses
                    .FirstOrDefault(addr => addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                                          !IPAddress.IsLoopback(addr.Address));

                if (ipv4 != null)
                {
                    return ipv4.Address;
                }
            }
        }
        catch
        {
            // Error getting local IP
        }

        return null;
    }

    public static void PrintPortForwardingInstructions(int port)
    {
        var localIP = GetLocalIPAddress();
        var publicIP = GetPublicIPAsync().GetAwaiter().GetResult();

        Console.WriteLine("\n=== Port Forwarding Setup ===");
        Console.WriteLine($"To allow connections from outside your network:");
        Console.WriteLine($"1. Log into your router's admin panel (usually 192.168.1.1 or 192.168.0.1)");
        Console.WriteLine($"2. Find 'Port Forwarding' or 'Virtual Server' settings");
        Console.WriteLine($"3. Add a new rule:");
        Console.WriteLine($"   - External Port: {port}");
        Console.WriteLine($"   - Internal Port: {port}");
        Console.WriteLine($"   - Protocol: TCP");
        if (localIP != null)
        {
            Console.WriteLine($"   - Internal IP: {localIP}");
        }
        else
        {
            Console.WriteLine($"   - Internal IP: [Your PC's local IP address]");
        }
        Console.WriteLine($"   - Description: PC Remote Server");
        Console.WriteLine($"4. Save the settings");
        
        if (publicIP != null)
        {
            Console.WriteLine($"\nYour public IP address: {publicIP}");
            Console.WriteLine($"After configuring port forwarding, connect using: {publicIP}:{port}");
        }
        else
        {
            Console.WriteLine($"\nNote: Could not detect public IP. You may need to check your router's status page.");
        }
        
        Console.WriteLine($"\nSecurity Note: Make sure your firewall allows incoming connections on port {port}");
        Console.WriteLine($"================================\n");
    }
}
