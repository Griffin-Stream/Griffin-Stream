using PCRemote.Server.Network;
using PCRemote.Server.ScreenCapture;
using PCRemote.Server.Input;
using PCRemote.Server.Security;
using PCRemote.Server.AudioCapture;
using System.Net;
using System.Net.Sockets;

namespace PCRemote.Server;

class Program
{
    private static TcpListener? _listener;
    private static bool _running = true;
    private static readonly CancellationTokenSource _cancellationTokenSource = new();
    private static int _shutdownRequested = 0; // For atomic check

    // DPI Awareness constants and imports
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(int dpiContext);
    
    private const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;


    static async Task Main(string[] args)
    {
        Console.WriteLine("PC Remote Server Starting...");
        Console.WriteLine("Press Ctrl+C to stop the server.");

        // Crash resilience: log unexpected exceptions instead of letting them terminate the
        // server. Background streaming tasks and the accept loop already guard themselves; these
        // are the last line of defense so a stray error can't take the whole process down.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Console.WriteLine($"[FATAL] Unhandled exception: {ex?.Message}");
            if (ex != null) Logging.CrashLogger.LogCrash("AppDomain.UnhandledException", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.WriteLine($"[WARN] Unobserved task exception: {e.Exception.Message}");
            Logging.CrashLogger.LogCrash("UnobservedTaskException", e.Exception);
            e.SetObserved(); // Prevent it from escalating to a process crash.
        };

        // CRITICAL: Set usage of Per-Monitor DPI Awareness V2 to ensure we get 
        // PHYSICAL screen coordinates (e.g. 3840x2160), not Scaled (e.g. 1920x1080).
        // This fixes cursor mapping issues where inputs would cover only 1/4 of the screen.
        try 
        {
            SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            Console.WriteLine("[System] Set Per-Monitor DPI Awareness V2");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[System] Failed to set DPI awareness: {ex.Message}");
        }

        // Initialize components
        var screenCapture = new ScreenCaptureService();
        var inputHandler = new InputHandler();
        var securityManager = new SecurityManager();
        // Initialize WASAPI audio capture (native Windows, no VB-Cable required)
        var audioCapture = new WasapiAudioCaptureService();

        // Start server
        const int port = 8888;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        Console.WriteLine($"Server listening on port {port}");

        // Advertise on the LAN so the app can auto-discover this PC (no manual IP entry).
        var discoveryResponder = new DiscoveryResponder(port);
        discoveryResponder.Start();

        // Optional system-tray presence (run minimized with a quick Exit). Enable with --tray.
        bool trayMode = args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        if (trayMode)
        {
            TrayIcon.Start(securityManager, port, () =>
            {
                _running = false;
                _cancellationTokenSource.Cancel();
                _listener?.Stop();
                Environment.Exit(0);
            });
            Console.WriteLine("[Tray] System-tray mode enabled (--tray).");
        }


        
        // Display connection information
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000, _cancellationTokenSource.Token); // Wait a bit for network
                var localIP = PortForwardingHelper.GetLocalIPAddress();
                
                Console.WriteLine($"\n=== Connection Information ===");
                if (localIP != null)
                {
                    Console.WriteLine($"Local IP: {localIP}");
                    Console.WriteLine($"Local network: Connect using {localIP}:{port}");
                }
                Console.WriteLine($"Port: {port}");
                Console.WriteLine($"--------------------------------");
                Console.WriteLine($"Pairing PIN (enter in the app to add a new device): {securityManager.PairingPin}");
                Console.WriteLine($"================================\n");
                
                // Check for ngrok tunnel
                var ngrokAddress = await TunnelHelper.GetNgrokTunnelAddress(port);
                if (ngrokAddress != null)
                {
                    Console.WriteLine($"\n✓ ngrok tunnel detected!");
                    Console.WriteLine($"  Connect using: {ngrokAddress}");
                    Console.WriteLine();
                }
                
                // Print tunnel instructions (for users without router access)
                TunnelHelper.PrintTunnelInstructions(port);
            }
            catch
            {
                // Ignore errors
            }
        });

        // Simple console commands for reviewing/revoking paired devices. Best-effort: skipped when
        // there is no interactive console (e.g. launched detached).
        if (!Console.IsInputRedirected)
        {
            _ = Task.Run(() =>
            {
                Console.WriteLine("Commands: 'devices' to list paired devices, 'remove <n>' to unpair one.");
                while (_running)
                {
                    string? line;
                    try { line = Console.ReadLine(); }
                    catch { break; }
                    if (line == null) break;
                    line = line.Trim();

                    if (line.Equals("devices", StringComparison.OrdinalIgnoreCase))
                    {
                        var devices = securityManager.ListDevices();
                        if (devices.Count == 0) { Console.WriteLine("No paired devices."); continue; }
                        for (int i = 0; i < devices.Count; i++)
                        {
                            var d = devices[i];
                            Console.WriteLine($"  [{i}] {d.Label} (paired {d.EnrolledUtc.ToLocalTime():g}, last seen {d.LastSeenUtc.ToLocalTime():g})");
                        }
                    }
                    else if (line.StartsWith("remove ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(line.Substring(7).Trim(), out int idx) && securityManager.RemoveDeviceByIndex(idx))
                            Console.WriteLine($"Removed device [{idx}] (it must pair again to reconnect).");
                        else
                            Console.WriteLine("Usage: remove <index shown by 'devices'>");
                    }
                }
            });
        }

        // Handle shutdown
        Console.CancelKeyPress += (sender, e) =>
        {
            var shutdownCount = Interlocked.Increment(ref _shutdownRequested);
            
            if (shutdownCount == 1)
            {
                // First Ctrl+C - graceful shutdown
                e.Cancel = true;
                _running = false;
                _cancellationTokenSource.Cancel();
                Console.WriteLine("\nShutting down server... (press Ctrl+C again to force exit)");
                _listener?.Stop();
                
                // Start a background task to force exit after 3 seconds if shutdown hangs
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    if (_shutdownRequested > 0)
                    {
                        Console.WriteLine("\nShutdown timeout - forcing exit...");
                        Environment.Exit(0);
                    }
                });
            }
            else if (shutdownCount == 2)
            {
                // Second Ctrl+C - force exit
                Console.WriteLine("\nForce exiting...");
                Environment.Exit(0);
            }
            else
            {
                // Already shutting down, force exit
                Environment.Exit(1);
            }
        };

        // Accept connections
        while (_running)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                client.NoDelay = true; // CRITICAL: Disable Nagle's algorithm for low latency streaming
                if (!_running)
                {
                    client.Close();
                    break;
                }
                _ = Task.Run(() => HandleClient(client, screenCapture, inputHandler, securityManager, audioCapture));
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (!_running)
            {
                // Expected when stopping the listener
                break;
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    Console.WriteLine($"Error accepting client: {ex.Message}");
                }
            }
        }

        _listener?.Stop();
        discoveryResponder.Dispose();
        TrayIcon.Stop();
        screenCapture.Dispose();
        audioCapture.Dispose();
        Console.WriteLine("Server stopped.");
    }

    private static async Task HandleClient(
        TcpClient client,
        ScreenCaptureService screenCapture,
        InputHandler inputHandler,
        SecurityManager securityManager,
        AudioCapture.WasapiAudioCaptureService audioCapture)
    {
        var remoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        Console.WriteLine($"Client connected: {remoteEndPoint}");
        
        try
        {
            Console.WriteLine($"Starting TLS handshake for {remoteEndPoint}...");
            var connection = new ClientConnection(client, screenCapture, inputHandler, securityManager, audioCapture);
            await connection.HandleAsync(_cancellationTokenSource.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling client {remoteEndPoint}: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
        finally
        {
            client.Close();
            Console.WriteLine($"Client disconnected: {remoteEndPoint}");
        }
    }
}
