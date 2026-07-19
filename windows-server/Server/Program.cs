using PCRemote.Server.Network;
using PCRemote.Server.ScreenCapture;
using PCRemote.Server.Input;
using PCRemote.Server.Security;
using PCRemote.Server.AudioCapture;
using PCRemote.Server.Licensing;
using PCRemote.Server.Logging;
using PCRemote.Server.Update;
using System.Net;
using System.Net.Sockets;

namespace PCRemote.Server;

class Program
{
    private static TcpListener? _listener;
    private static bool _running = true;
    private static readonly CancellationTokenSource _cancellationTokenSource = new();
    private static int _shutdownRequested = 0; // For atomic check

    // Cap concurrent TCP clients (includes in-flight TLS/auth handshakes). Streaming is still
    // single-active ("newest wins"); this only limits resource use from connection floods.
    private static int _activeClients;
    private const int MaxConcurrentClients = 8;

    // DPI Awareness constants and imports
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(int dpiContext);
    
    private const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;


    static async Task Main(string[] args)
    {
        // Route all console output to a rolling log file + in-memory buffer so the GUI's
        // "Show debug log" toggle can attach a terminal and replay history on demand. This
        // must run before the first Console.WriteLine so nothing is missed.
        ConsoleTee.Install();

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

        // Resolve the Free/Pro tier (BETA_FREE_PRO flag, or a cached/online-validated license) before
        // any client connects. Keep an active client's tier in sync if a license is activated at runtime.
        await LicenseManager.InitializeAsync();
        LicenseManager.TierChanged += _ => ClientConnection.ResendServerInfoToActive();
        Console.WriteLine($"[License] Server tier: {LicenseManager.CurrentTier} ({LicenseManager.StatusText})");

        // Headless activation for --no-window installs: --license <key>. GUI users use the Activate Pro box.
        var licenseArgIdx = Array.FindIndex(args, a => string.Equals(a, "--license", StringComparison.OrdinalIgnoreCase));
        if (licenseArgIdx >= 0 && licenseArgIdx + 1 < args.Length)
        {
            var (_, licMsg) = await LicenseManager.ActivateAsync(args[licenseArgIdx + 1]);
            Console.WriteLine($"[License] {licMsg}");
        }

        // Start server
        const int port = 8888;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        Console.WriteLine($"Server listening on port {port}");

        // Advertise on the LAN so the app can auto-discover this PC (no manual IP entry).
        var discoveryResponder = new DiscoveryResponder(port);
        discoveryResponder.Start();

        // Show the pairing PIN + connection address in a clear, always-on-top window so users don't
        // have to hunt for the PIN in the scrolling console. Opt out with --no-window (e.g. headless).
        bool showWindow = !args.Any(a => string.Equals(a, "--no-window", StringComparison.OrdinalIgnoreCase));
        if (showWindow)
        {
            // Closing the dashboard stops the server (users can minimize to keep it running).
            ServerDashboard.Start(securityManager, port, RequestShutdown);
        }

        // Best-effort check for a newer release. The GUI surfaces an "Update available" affordance
        // via Updater.UpdateFound; this never blocks startup and is silent when offline/up to date.
        _ = Updater.CheckAsync();

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

                // Fill in the real address on the dashboard now that we know the local IP.
                ServerDashboard.SetLocalIp(localIP?.ToString(), port);

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

        // Simple console commands for reviewing/revoking paired devices. Only active while the
        // debug terminal is attached (WinExe has no console by default). When the terminal is
        // hidden we idle instead of busy-looping on a dead stdin.
        _ = Task.Run(() =>
            {
                bool announced = false;
                while (_running)
                {
                    if (!ConsoleTee.IsConsoleVisible)
                    {
                        announced = false;
                        Thread.Sleep(400);
                        continue;
                    }
                    if (!announced)
                    {
                        Console.WriteLine("Commands: 'devices' to list paired devices, 'remove <n>' to unpair one.");
                        announced = true;
                    }

                    string? line;
                    try { line = Console.ReadLine(); }
                    catch { Thread.Sleep(400); continue; }
                    // Null means the console was detached (Show debug log toggled off) - keep the
                    // server running and wait for it to come back rather than exiting the loop.
                    if (line == null) { Thread.Sleep(400); continue; }
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

                if (Volatile.Read(ref _activeClients) >= MaxConcurrentClients)
                {
                    Console.WriteLine($"[Server] Rejecting client — already at {MaxConcurrentClients} concurrent connections.");
                    client.Close();
                    continue;
                }

                Interlocked.Increment(ref _activeClients);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleClient(client, screenCapture, inputHandler, securityManager, audioCapture);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeClients);
                    }
                });
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

    /// <summary>Gracefully stop the server and exit the process. Invoked when the dashboard closes.</summary>
    private static void RequestShutdown()
    {
        _running = false;
        try { _cancellationTokenSource.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        Console.WriteLine("Dashboard closed — stopping server.");
        Environment.Exit(0);
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
