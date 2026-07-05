using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PCRemote.Server.Network;

/// <summary>
/// Answers LAN discovery probes so the mobile app can find this PC without the user typing an
/// IP address. Listens for a small UDP broadcast probe and replies (unicast) with the machine
/// name and the server's TCP port. Best-effort: failures are logged and never crash the server.
/// </summary>
public class DiscoveryResponder : IDisposable
{
    public const int DiscoveryPort = 8889;
    private const string ProbeMagic = "GRIFFIN_DISCOVER_V1";
    private const string ResponsePrefix = "GRIFFIN_HERE_V1";

    private readonly int _servicePort;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public DiscoveryResponder(int servicePort)
    {
        _servicePort = servicePort;
    }

    public void Start()
    {
        try
        {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => ListenLoopAsync(_cts.Token));
            Console.WriteLine($"[Discovery] Listening for LAN discovery on UDP {DiscoveryPort}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Discovery] Responder disabled: {ex.Message}");
        }
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        if (_udp == null) return;
        string hostName = Environment.MachineName;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(token);
                string text = Encoding.UTF8.GetString(result.Buffer);
                if (!text.StartsWith(ProbeMagic, StringComparison.Ordinal)) continue;

                string payload = $"{ResponsePrefix}|{hostName}|{_servicePort}";
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                await _udp.SendAsync(bytes, bytes.Length, result.RemoteEndPoint);
                Console.WriteLine($"[Discovery] Replied to probe from {result.RemoteEndPoint}");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    Console.WriteLine($"[Discovery] Error: {ex.Message}");
                    try { await Task.Delay(500, token); } catch { break; }
                }
            }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Close(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _udp = null;
    }
}
