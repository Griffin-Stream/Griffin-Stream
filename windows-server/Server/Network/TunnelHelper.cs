using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace PCRemote.Server.Network;

/// <summary>
/// Helper for setting up reverse tunnels using services like ngrok, Cloudflare Tunnel, etc.
/// This allows connections without router access or port forwarding.
/// </summary>
public class TunnelHelper
{
    public static void PrintTunnelInstructions(int port)
    {
        var localIP = PortForwardingHelper.GetLocalIPAddress();
        
        Console.WriteLine("\n=== Remote Access Setup ===");
        Console.WriteLine("For connections from outside your local network:\n");
        
        Console.WriteLine("RECOMMENDED: Tailscale");
        Console.WriteLine("  1. Install Tailscale on this PC: https://tailscale.com/download");
        Console.WriteLine("  2. Install Tailscale on your Android device");
        Console.WriteLine("  3. Sign in with the same account on both devices");
        Console.WriteLine("  4. Find your Tailscale IP in the Tailscale app (e.g., 100.64.1.2)");
        Console.WriteLine("  5. Connect using: [Tailscale IP]:" + port);
        Console.WriteLine("  6. Works automatically through NAT - no router configuration needed\n");
        
        Console.WriteLine("Alternative: ngrok (Quick Setup)");
        Console.WriteLine("  1. Sign up at https://ngrok.com (free tier available)");
        Console.WriteLine("  2. Download ngrok and run: ngrok tcp " + port);
        Console.WriteLine("  3. Use the ngrok address shown to connect\n");
        
        Console.WriteLine("Alternative: Cloudflare Tunnel");
        Console.WriteLine("  1. Install cloudflared: https://developers.cloudflare.com/cloudflare-one/connections/connect-apps/install-and-setup/");
        Console.WriteLine("  2. Run: cloudflared tunnel --url tcp://localhost:" + port);
        Console.WriteLine("  3. Use the address shown to connect\n");
        
        if (localIP != null)
        {
            Console.WriteLine($"Current local IP: {localIP}");
        }
        Console.WriteLine($"Server port: {port}");
        Console.WriteLine("================================================\n");
    }

    public static async Task<string?> GetNgrokTunnelAddress(int port)
    {
        try
        {
            // Try to get ngrok tunnel info from local API
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(2);
            var response = await client.GetStringAsync("http://127.0.0.1:4040/api/tunnels");
            
            using var doc = JsonDocument.Parse(response);
            var tunnels = doc.RootElement.GetProperty("tunnels");
            
            foreach (var tunnel in tunnels.EnumerateArray())
            {
                var config = tunnel.GetProperty("config");
                var addr = tunnel.GetProperty("public_url").GetString();
                var proto = tunnel.GetProperty("proto").GetString();
                
                if (proto == "tcp" && addr != null)
                {
                    return addr;
                }
            }
        }
        catch
        {
            // ngrok API not available or not running
        }
        
        return null;
    }
}
