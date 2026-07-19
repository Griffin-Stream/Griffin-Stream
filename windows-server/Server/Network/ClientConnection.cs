using System.Net.Security;
using System.Buffers;
using System.Net.Sockets;
using PCRemote.Server.ScreenCapture;
using PCRemote.Server.Input;
using PCRemote.Server.Security;
using PCRemote.Server.WOL;
using PCRemote.Server.AudioCapture;
using PCRemote.Shared.Protocol;
using PCRemote.Server.Logging;
using PCRemote.Server.Licensing;

namespace PCRemote.Server.Network;

public class ClientConnection
{
    // Raise the system timer resolution to ~1ms while streaming so Task.Delay-based frame
    // pacing is accurate at 60+ fps (default Windows granularity is ~15ms).
    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);
    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    private readonly TcpClient _client;
    private Stream _stream;
    private readonly ScreenCaptureService _screenCapture;
    private readonly InputHandler _inputHandler;
    private readonly SecurityManager _securityManager;
    private readonly WasapiAudioCaptureService _audioCapture;
    private ProtocolHandler? _protocolHandler;
    private bool _authenticated = false;

    // Per-connection challenge-response state. The server issues a single-use nonce, then the
    // client must return a valid ECDSA signature over it before _authenticated flips true.
    private byte[]? _pendingNonce;
    private byte[]? _pendingPubKey;
    private string _pendingLabel = "Device";
    private bool _pendingIsPairing;
    private byte[]? _authenticatedPubKey; // enrolled key of this session (for unpair/last-seen)

    private const byte AuthTypeKeyBegin = 0x10;
    private const byte AuthTypePairBegin = 0x11;
    private readonly SemaphoreSlim _writeSemaphore = new SemaphoreSlim(1, 1); // Serialize all writes to the stream
    private System.Diagnostics.Stopwatch? _streamStopwatch; // Tracks stream start time for A/V sync timestamps
    private CancellationToken _cancellationToken; // Store for use in streaming tasks
    private CancellationTokenSource? _sessionCts; // Per-session cancellation (linked to server token)

    // Single active streaming session: the shared screen/audio capture can only serve one
    // client at a time, so a newly authenticated client takes over ("newest wins") and the
    // previous one is cleanly ended instead of two clients fighting over one encoder.
    private static readonly object _sessionLock = new();
    private static ClientConnection? _activeConnection;

    /// <summary>End this client's session because another device took over (or shutdown).</summary>
    private void EndSession(string reason)
    {
        Console.WriteLine($"[Session] Ending session: {reason}");
        try { _sessionCts?.Cancel(); } catch { }
        try { _client.Close(); } catch { } // Unblocks the read loop so HandleAsync returns.
    }

    /// &lt;summary&gt;
    /// Writes a 64-bit timestamp in big-endian (network byte order) into a buffer at the given offset.
    /// &lt;/summary&gt;
    private static void WriteBigEndianTimestamp(byte[] buffer, int offset, long timestampUs)
    {
        buffer[offset]     = (byte)(timestampUs >> 56);
        buffer[offset + 1] = (byte)(timestampUs >> 48);
        buffer[offset + 2] = (byte)(timestampUs >> 40);
        buffer[offset + 3] = (byte)(timestampUs >> 32);
        buffer[offset + 4] = (byte)(timestampUs >> 24);
        buffer[offset + 5] = (byte)(timestampUs >> 16);
        buffer[offset + 6] = (byte)(timestampUs >> 8);
        buffer[offset + 7] = (byte)timestampUs;
    }

    public ClientConnection(
        TcpClient client,
        ScreenCaptureService screenCapture,
        InputHandler inputHandler,
        SecurityManager securityManager,
        WasapiAudioCaptureService audioCapture)
    {
        _client = client;
        _stream = client.GetStream();
        _screenCapture = screenCapture;
        _inputHandler = inputHandler;
        _securityManager = securityManager;
        _audioCapture = audioCapture;
    }

    public async Task HandleAsync(CancellationToken cancellationToken)
    {
        // Session token is linked to the server token but can also be cancelled independently
        // when another client takes over this session.
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellationToken = _sessionCts.Token; // Store for streaming tasks

        try
        {
            Console.WriteLine("Performing TLS handshake...");
            // Perform TLS handshake
            _stream = await _securityManager.EstablishSecureConnection(_stream, cancellationToken);
            Console.WriteLine("TLS handshake completed successfully");
            _protocolHandler = new ProtocolHandler(_stream, _writeSemaphore);
            Console.WriteLine("Protocol handler created, waiting for messages...");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during TLS handshake: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                // A client-side certificate-pin mismatch shows up here as the peer aborting the
                // handshake. This is expected if the server certificate changed.
                Console.WriteLine("  If the server certificate recently changed, clear the app's saved");
                Console.WriteLine("  server certificate (re-pair) so the phone can pin the new one.");
            }
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            CrashLogger.LogCrash("TLS Handshake", ex);
            throw;
        }

        // Drop peers that complete TLS but never finish ECDSA auth (resource / DoS guard).
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), _cancellationToken);
                if (!_authenticated && _client.Connected)
                {
                    Console.WriteLine("[ClientConnection] Authentication timed out; closing connection.");
                    try { _client.Close(); } catch { /* ignore */ }
                }
            }
            catch (OperationCanceledException) { /* session ended */ }
        });

        try
        {
            // Handle incoming messages
            while (!_cancellationToken.IsCancellationRequested && _client.Connected)
            {
                try
                {
                    var message = await _protocolHandler.ReadMessageAsync(_cancellationToken);
                    await HandleMessage(message);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Session ended (cancelled or taken over by another device)");
                    break;
                }
                catch (System.IO.EndOfStreamException)
                {
                    Console.WriteLine("Client disconnected (end of stream)");
                    break;
                }
                catch (System.IO.IOException ex) when (ex.Message.Contains("end of stream") || ex.Message.Contains("forcibly closed"))
                {
                    Console.WriteLine("Client disconnected (connection closed)");
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error reading message: {ex.Message}");
                    CrashLogger.LogCrash("Message Reading", ex);
                    break;
                }
            }
        }
        finally
        {
            // Relinquish the active-session slot if we still hold it.
            lock (_sessionLock)
            {
                if (_activeConnection == this) _activeConnection = null;
            }
            try { _sessionCts?.Dispose(); } catch { }
        }
    }

    private async Task StreamScreenFrames(CancellationToken cancellationToken)
    {
        // Wait for stopwatch to be initialized (audio initialization completes first)
        // This ensures video and audio timestamps start at the same time
        int waitAttempts = 0;
        const int maxWaitAttempts = 200; // Wait up to 20 seconds for audio initialization
        while (_streamStopwatch == null && waitAttempts < maxWaitAttempts && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(100, cancellationToken);
            waitAttempts++;
        }
        
        if (_streamStopwatch == null)
        {
            Console.WriteLine("[ClientConnection] WARNING: Stream stopwatch not initialized after waiting. Video timestamps may be incorrect.");
        }
        
        int frameCount = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var lastFrameTime = stopwatch.ElapsedMilliseconds;
        var pendingSends = new List<Task>(); // Local list for thread safety
        bool? lastIdleReported = null; // track active/idle transitions to notify the client

        // Frame pacing: honor the client's requested fps cap. Without pacing the loop runs as
        // fast as the capture/encode/send pipeline allows (150+ fps), ignoring the negotiated cap
        // and wasting CPU/GPU/bandwidth.
        //
        // We schedule capture on an *absolute* cadence (nextCaptureTicks advances by one frame
        // budget each iteration) so capture/encode time is absorbed within the budget rather than
        // added on top of it. The wait is a hybrid: Thread.Sleep(1) (accurate to ~1ms thanks to
        // timeBeginPeriod(1)) for the bulk, then a short spin for the final ~1.5ms. Plain
        // Task.Delay snaps to the ~15.6ms scheduler tick, which made 60 fps behave like 30.
        long freq = System.Diagnostics.Stopwatch.Frequency;
        long spinMarginTicks = freq / 700; // ~1.4ms spin tail for sub-tick accuracy
        long nextCaptureTicks = stopwatch.ElapsedTicks;

        TimeBeginPeriod(1);
        try
        {
        while (!cancellationToken.IsCancellationRequested && _client.Connected && _authenticated)
        {
            try
            {
                // Pace to the requested fps cap before capturing a fresh frame to send now.
                int targetFps = _screenCapture.TargetFps;
                if (targetFps > 0)
                {
                    long budgetTicks = freq / targetFps;
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        long remaining = nextCaptureTicks - stopwatch.ElapsedTicks;
                        if (remaining <= 0) break;
                        if (remaining > spinMarginTicks)
                            System.Threading.Thread.Sleep(1);
                        else
                            System.Threading.Thread.SpinWait(80);
                    }
                    // Advance to the next slot. If a slow encode/network put us behind schedule,
                    // resync to now so we don't build up debt and then burst above the cap.
                    nextCaptureTicks += budgetTicks;
                    long nowTicks = stopwatch.ElapsedTicks;
                    if (nextCaptureTicks < nowTicks) nextCaptureTicks = nowTicks;
                }
                else
                {
                    nextCaptureTicks = stopwatch.ElapsedTicks;
                }

                // Tell the client when the stream flips between active and idle (static screen),
                // so its FPS HUD can show a stable "Idle" state instead of a jittery low number.
                if (_protocolHandler != null)
                {
                    bool nowIdle = _screenCapture.IsScreenIdle;
                    if (lastIdleReported != nowIdle)
                    {
                        lastIdleReported = nowIdle;
                        try
                        {
                            await _protocolHandler.WriteMessageAsync(new ProtocolMessage
                            {
                                Type = MessageType.StreamState,
                                Data = new byte[] { (byte)(nowIdle ? 1 : 0) }
                            }, cancellationToken);
                        }
                        catch { /* non-fatal; will retry on next transition */ }
                    }
                }

                var frameStartTime = stopwatch.ElapsedMilliseconds;
                // Timestamp BEFORE capture to represent when the frame content was captured
                // This ensures A/V sync is based on content time, not processing time
                var timestampUs = _streamStopwatch != null ? _streamStopwatch.ElapsedTicks * 1_000_000L / System.Diagnostics.Stopwatch.Frequency : 0L;
                var frame = await _screenCapture.CaptureFrameAsync(cancellationToken);
                
                if (frame != null && frame.Length > 0 && _protocolHandler != null)
                {
                    // Log frame size occasionally
                    if (frameCount % 120 == 0) // Every 120 frames
                    {
                        var elapsed = stopwatch.ElapsedMilliseconds - lastFrameTime;
                        var fps = frameCount > 0 ? (frameCount * 1000.0 / elapsed) : 0;
                        Console.WriteLine($"Sending frame {frameCount}: {frame.Length / 1024} KB ({frame.Length} bytes), FPS: {fps:F1}");
                        lastFrameTime = stopwatch.ElapsedMilliseconds;
                        frameCount = 0; // Reset counter for next measurement
                    }
                    
                    // Use timestamp captured before frame capture for accurate A/V sync
                    var sendTimeUs = _streamStopwatch != null ? _streamStopwatch.ElapsedTicks * 1_000_000L / System.Diagnostics.Stopwatch.Frequency : 0L;
                    
                    // Log sync info occasionally for debugging
                    if (frameCount % 120 == 0 && frameCount > 0)
                    {
                        Console.WriteLine($"[SYNC] Video frame {frameCount}: timestamp={timestampUs / 1000}ms, sendTime={sendTimeUs / 1000}ms");
                    }
                    
                    var frameWithTimestamp = new byte[8 + frame.Length];
                    // Write timestamp as big-endian (network byte order)
                    WriteBigEndianTimestamp(frameWithTimestamp, 0, timestampUs);
                    Array.Copy(frame, 0, frameWithTimestamp, 8, frame.Length);
                    
                    var message = new ProtocolMessage
                    {
                        Type = MessageType.ScreenFrame,
                        Data = frameWithTimestamp
                    };
                    
                    try
                    {
                        // Pipeline: Send frame in background, immediately capture next
                        // This allows Capture/Encode and Send to happen in parallel
                        // We don't await the send, but we track it to avoid memory buildup
                        
                        // Simple flow control: don't buffer too many frames if network is slow
                        if (pendingSends.Count > 3)
                        {
                            var finishedTask = await Task.WhenAny(pendingSends);
                            pendingSends.Remove(finishedTask);
                            // Propagate exceptions
                            await finishedTask; 
                        }
                        
                        var sendTask = _protocolHandler.WriteMessageAsync(message, cancellationToken);
                        pendingSends.Add(sendTask);

                        // Clean up finished tasks occasionally to keep list small
                        if (frameCount % 10 == 0)
                        {
                            pendingSends.RemoveAll(t => t.IsCompleted);
                        }

                        frameCount++;
                        
                        // No rate limiting - speed is limited by Capture+Encode+Send pipeline capacity
                        // With parallel sending, this should hit max FPS of the encoder/network
                        if (frameCount % 60 == 0)
                        {
                             // Occasionally clean up the list fully
                            pendingSends.RemoveAll(t => t.IsCompleted);
                        }
                    }
                    catch (System.IO.IOException ex) when (ex.Message.Contains("forcibly closed") || ex.Message.Contains("broken pipe"))
                    {
                        Console.WriteLine("Client disconnected during frame send");
                        break;
                    }
                }
                else if (frame == null)
                {
                    // Frame capture returned null - no new frame available
                    // Use yield to allow other tasks to run, but don't delay unnecessarily
                    // This maximizes capture rate while preventing CPU spinning
                    await Task.Yield();
                }
                else if (frame.Length == 0)
                {
                    // Empty frame - this is now normal (intentional skip to maintain low latency)
                    // Do nothing, just loop to next frame
                    await Task.Yield();
                }
            }
            catch (System.IO.IOException ex) when (ex.Message.Contains("forcibly closed") || ex.Message.Contains("broken pipe"))
            {
                Console.WriteLine("Client disconnected (connection closed)");
                break;
            }
            catch (OperationCanceledException)
            {
                break; // Normal shutdown
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error streaming frame: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
                }
                CrashLogger.LogCrash("StreamScreenFrames Loop", ex);
                break;
            }
        }
        }
        finally
        {
            TimeEndPeriod(1);
        }
        stopwatch.Stop();
        Console.WriteLine($"Stopped streaming frames. Total frames sent: {frameCount}");
    }

    private async Task StreamCursorPosition(CancellationToken cancellationToken)
    {
        var lastX = -1;
        var lastY = -1;
        var lastVisible = true;
        
        // Polling configuration
        const int ActivePollingInterval = 8;   // 8ms = ~125Hz (Standard gaming mouse rate)
        const int IdlePollingInterval = 33;    // 33ms = ~30Hz (Responsive enough for initial movement)
        const int ActiveGracePeriodMs = 500;   // Keep high polling rate for 500ms after last movement
        
        var lastMovementTime = DateTime.MinValue;

        // Send initial cursor position immediately on connection
        try
        {
            var (initialX, initialY, initialVisible) = Input.InputHandler.GetCursorState();
            var (monitorLeft, monitorTop) = _screenCapture.GetCurrentMonitorOffset();
            
            // Normalize relative to the specific monitor/region being captured
            initialX -= monitorLeft;
            initialY -= monitorTop;
            
            if (_protocolHandler != null)
            {
                var data = new byte[9];
                // Send as big-endian (network byte order) to match Android client
                data[0] = (byte)(initialX >> 24);
                data[1] = (byte)(initialX >> 16);
                data[2] = (byte)(initialX >> 8);
                data[3] = (byte)initialX;
                data[4] = (byte)(initialY >> 24);
                data[5] = (byte)(initialY >> 16);
                data[6] = (byte)(initialY >> 8);
                data[7] = (byte)initialY;
                data[8] = 1; // Initial visibility always true
                
                var message = new ProtocolMessage
                {
                    Type = MessageType.CursorPosition,
                    Data = data
                };
                
                await _protocolHandler.WriteMessageAsync(message, cancellationToken);
                lastX = initialX;
                lastY = initialY;
                Console.WriteLine($"[ClientConnection] Sent initial cursor position: ({initialX}, {initialY}), visible: true");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ClientConnection] Error sending initial cursor position: {ex.Message}");
        }
        
        while (!cancellationToken.IsCancellationRequested && _client.Connected && _authenticated)
        {
            try
            {
                var (x, y, visible) = Input.InputHandler.GetCursorState();
                
                // Get offset of the specific monitor being captured to normalize coordinates
                // Client expects coordinates relative to the top-left of the video frame
                var (monitorLeft, monitorTop) = _screenCapture.GetCurrentMonitorOffset();
                
                // Normalize to 0-based coordinates relative to the captured monitor
                var normalizedX = x - monitorLeft;
                var normalizedY = y - monitorTop;
                
                // Only send if position OR visibility changed
                if (normalizedX != lastX || normalizedY != lastY || visible != lastVisible)
                {
                    lastX = normalizedX;
                    lastY = normalizedY;
                    lastVisible = visible;
                    lastMovementTime = DateTime.UtcNow; // Mark activity
                    
                    if (_protocolHandler != null)
                    {
                        var data = new byte[9];
                        // Send as big-endian (network byte order) to match Android client
                        data[0] = (byte)(normalizedX >> 24);
                        data[1] = (byte)(normalizedX >> 16);
                        data[2] = (byte)(normalizedX >> 8);
                        data[3] = (byte)normalizedX;
                        data[4] = (byte)(normalizedY >> 24);
                        data[5] = (byte)(normalizedY >> 16);
                        data[6] = (byte)(normalizedY >> 8);
                        data[7] = (byte)normalizedY;
                        data[8] = (byte)(visible ? 1 : 0);
                        
                        var message = new ProtocolMessage
                        {
                            Type = MessageType.CursorPosition,
                            Data = data
                        };
                        
                        // Use fire-and-forget to avoid blocking the polling loop
                        _ = _protocolHandler.WriteMessageAsync(message, cancellationToken);
                    }
                }
                
                // Adaptive polling logic
                // If we moved recently (within grace period), use fast polling
                // Otherwise use idle polling
                var timeSinceLastMove = (DateTime.UtcNow - lastMovementTime).TotalMilliseconds;
                int pollDelay = (timeSinceLastMove < ActiveGracePeriodMs) ? ActivePollingInterval : IdlePollingInterval;
                
                await Task.Delay(pollDelay, cancellationToken);
            }
            catch (System.IO.IOException ex) when (ex.Message.Contains("forcibly closed") || ex.Message.Contains("broken pipe"))
            {
                Console.WriteLine("Client disconnected during cursor position send");
                break;
            }
            catch (Exception)
            {
                // Don't break on error, just log and continue
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    // Pre-auth abuse: close the socket after too many non-auth messages.
    private int _preAuthViolations;
    private const int MaxPreAuthViolations = 8;

    // WOL rate limit (per connection) — prevents magic-packet spam from a paired device.
    private DateTime _lastWolUtc = DateTime.MinValue;
    private static readonly TimeSpan WolMinInterval = TimeSpan.FromSeconds(5);

    private async Task HandleMessage(ProtocolMessage message)
    {
        // Handshake-only allowlist before authentication. Everything else (including screen
        // config, bitrate, WOL, heartbeats) requires a completed ECDSA challenge-response.
        switch (message.Type)
        {
            case MessageType.AuthRequest:
                await HandleAuthRequest(message);
                return;
            case MessageType.AuthResponse:
                await HandleAuthResponse(message);
                return;
        }

        if (!_authenticated)
        {
            _preAuthViolations++;
            Console.WriteLine($"[ClientConnection] Ignoring {message.Type} before authentication (violation {_preAuthViolations}/{MaxPreAuthViolations}).");
            if (_preAuthViolations >= MaxPreAuthViolations)
            {
                Console.WriteLine("[ClientConnection] Too many pre-auth messages; closing connection.");
                try { _client.Close(); } catch { /* ignore */ }
            }
            return;
        }

        switch (message.Type)
        {
            case MessageType.Unpair:
                await HandleUnpair();
                break;
            case MessageType.MouseInput:
                {
                    var (monitorLeft, monitorTop) = _screenCapture.GetCurrentMonitorOffset();
                    _inputHandler.HandleMouseInput(message.Data ?? Array.Empty<byte>(), monitorLeft, monitorTop);
                }
                break;
            case MessageType.KeyboardInput:
                _inputHandler.HandleKeyboardInput(message.Data ?? Array.Empty<byte>());
                break;
            case MessageType.GamepadInput:
                _inputHandler.HandleGamepadInput(message.Data ?? Array.Empty<byte>());
                break;
            case MessageType.TextInput:
                if (message.Data != null)
                {
                    var text = ProtocolSerializer.DeserializeTextInput(message.Data);
                    _inputHandler.HandleTextInput(text);
                }
                break;
            case MessageType.Heartbeat:
                if (_protocolHandler != null)
                {
                    await _protocolHandler.WriteMessageAsync(new ProtocolMessage
                    {
                        Type = MessageType.HeartbeatResponse
                    }, CancellationToken.None);
                }
                break;
            case MessageType.WOLRequest:
                // Auth required: server must already be reachable, so this wakes another host
                // (or a MAC the user saved) from a paired device — not an unauthenticated LAN open.
                if (message.Data != null && message.Data.Length > 0)
                {
                    var now = DateTime.UtcNow;
                    if (now - _lastWolUtc < WolMinInterval)
                    {
                        Console.WriteLine("[ClientConnection] WOL rate-limited; ignoring.");
                        break;
                    }
                    _lastWolUtc = now;
                    var macAddress = System.Text.Encoding.UTF8.GetString(message.Data);
                    Console.WriteLine($"[ClientConnection] Received WOL request for MAC: {macAddress}");
                    var success = WOLHelper.SendMagicPacket(macAddress);
                    Console.WriteLine(success
                        ? $"[ClientConnection] Successfully sent WOL packet for {macAddress}"
                        : $"[ClientConnection] Failed to send WOL packet for {macAddress}");
                }
                break;
            case MessageType.QualityChange:
                // Handle zoom scale change for higher quality encoding
                if (message.Data != null && message.Data.Length >= 4)
                {
                    float zoomScale;
                    if (BitConverter.IsLittleEndian)
                    {
                        // Copy bytes before reversing to avoid mutating the original message data
                        var floatBytes = new byte[4];
                        Array.Copy(message.Data, 0, floatBytes, 0, 4);
                        Array.Reverse(floatBytes);
                        zoomScale = BitConverter.ToSingle(floatBytes, 0);
                    }
                    else
                    {
                        zoomScale = BitConverter.ToSingle(message.Data, 0);
                    }
                    
                    // Validate zoom scale to prevent crashes from corrupted data
                    if (zoomScale >= 0.5f && zoomScale <= 10.0f) // Reasonable range
                    {
                        _screenCapture.SetZoomScale(zoomScale);
                        Console.WriteLine($"[ClientConnection] Zoom scale changed to: {zoomScale:F2}x - Quality mode activated");
                    }
                    else
                    {
                        Console.WriteLine($"[ClientConnection] Received invalid zoom scale: {zoomScale:F2}x. Ignoring.");
                    }
                }
                break;
            case MessageType.ScreenConfig:
                // Client-negotiated video configuration (codec, bitrate, fps, resolution, quality).
                if (message.Data != null && message.Data.Length >= ScreenConfigData.Size)
                {
                    try
                    {
                        var cfg = ProtocolSerializer.DeserializeScreenConfig(message.Data);
                        // Server-side enforcement: a Free server clamps the requested quality to the
                        // free ceiling regardless of what the client asks for (a modified/old app can
                        // request more, but the server won't honor it). Pro servers honor the full range.
                        if (LicenseManager.CurrentTier != ServerTier.Pro)
                        {
                            ClampConfigToFree(cfg);
                            Console.WriteLine("[ClientConnection] Free tier - clamped ScreenConfig to free ceiling (H264, <=15Mbps, <=60fps, <=1080p, 8-bit).");
                        }
                        bool wantHevc = cfg.Codec == ScreenConfigData.CodecHevc &&
                                        (cfg.Capabilities & ScreenConfigData.CapHevc) != 0;
                        int bitDepth = (cfg.BitDepth == 10 && (cfg.Capabilities & ScreenConfigData.CapMain10) != 0) ? 10 : 8;
                        _screenCapture.SetVideoConfig(
                            useHevc: wantHevc,
                            bitDepth: bitDepth,
                            qualityMode: cfg.QualityMode,
                            fpsCap: cfg.FpsCap,
                            bitrateKbps: (int)cfg.BitrateKbps,
                            resolutionMode: cfg.ResolutionMode,
                            targetWidth: cfg.TargetWidth,
                            targetHeight: cfg.TargetHeight);
                        Console.WriteLine($"[ClientConnection] Applied ScreenConfig from client (codec={(wantHevc ? "HEVC" : "H264")}, {bitDepth}-bit, {cfg.BitrateKbps}kbps, {cfg.FpsCap}fps, q={cfg.QualityMode}, res={cfg.ResolutionMode})");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ClientConnection] Failed to apply ScreenConfig: {ex.Message}");
                    }
                }
                break;
            case MessageType.BitrateChange:
                // Runtime (adaptive) bitrate change in kbps (big-endian u32).
                if (message.Data != null && message.Data.Length >= 4)
                {
                    try
                    {
                        uint kbps = ProtocolSerializer.DeserializeBitrateChange(message.Data);
                        if (kbps >= 500 && kbps <= 200000) // 0.5 - 200 Mbps sanity range
                        {
                            _screenCapture.SetRuntimeBitrate((int)kbps);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ClientConnection] Failed to apply BitrateChange: {ex.Message}");
                    }
                }
                break;
            case MessageType.MonitorSelect:
                // Handle monitor selection from client
                if (message.Data != null && message.Data.Length >= 1)
                {
                    // First byte is signed: -1 = all, 0 = primary, 1+ = secondary
                    sbyte monitorIndex = (sbyte)message.Data[0];
                    Console.WriteLine($"[ClientConnection] Received monitor select request: {monitorIndex}");
                    _screenCapture.SetSelectedMonitor(monitorIndex);
                    
                    // Send updated monitor info back to client
                    await SendMonitorInfo();
                }
                break;
            // EncodingPreference removed - lossless encoding is now automatic when zoomed > 1.3x
        }
    }
    
    /// <summary>Clamp a client-requested video config down to the free tier ceiling (server-enforced).</summary>
    private static void ClampConfigToFree(ScreenConfigData cfg)
    {
        cfg.Codec = ScreenConfigData.CodecH264;
        cfg.BitDepth = 8;
        if (cfg.FpsCap > 60) cfg.FpsCap = 60;
        if (cfg.BitrateKbps > 15000) cfg.BitrateKbps = 15000;
        // Cap resolution to 1080p: native and 1440p drop to 1080p.
        if (cfg.ResolutionMode == ScreenConfigData.ResNative || cfg.ResolutionMode == ScreenConfigData.Res1440)
            cfg.ResolutionMode = ScreenConfigData.Res1080;
        // Match-device resolutions above 1080p are pinned to 1080p.
        if (cfg.ResolutionMode == ScreenConfigData.ResMatchDevice &&
            (cfg.TargetHeight == 0 || cfg.TargetHeight > 1080))
        {
            cfg.TargetWidth = 1920;
            cfg.TargetHeight = 1080;
        }
    }

    /// <summary>Tell the client whether this is a Free or Pro server (plus server/protocol version).</summary>
    private async Task SendServerInfo()
    {
        if (_protocolHandler == null) return;
        try
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            var info = new ServerInfoData
            {
                Tier = LicenseManager.CurrentTier == ServerTier.Pro ? ServerInfoData.TierPro : ServerInfoData.TierFree,
                VersionMajor = (byte)Math.Clamp(v.Major, 0, 255),
                VersionMinor = (byte)Math.Clamp(v.Minor, 0, 255),
                VersionPatch = (byte)Math.Clamp(v.Build < 0 ? 0 : v.Build, 0, 255),
                ProtocolVersion = 1,
            };
            await _protocolHandler.WriteMessageAsync(new ProtocolMessage
            {
                Type = MessageType.ServerInfo,
                Data = ProtocolSerializer.SerializeServerInfo(info)
            }, CancellationToken.None);
            Console.WriteLine($"[ClientConnection] Sent ServerInfo (tier={LicenseManager.CurrentTier}).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ClientConnection] Error sending server info: {ex.Message}");
        }
    }

    /// <summary>Resend ServerInfo to the currently active session (e.g. after a live license activation).</summary>
    public static void ResendServerInfoToActive()
    {
        ClientConnection? active;
        lock (_sessionLock) { active = _activeConnection; }
        if (active != null) _ = active.SendServerInfo();
    }

    private async Task SendMonitorInfo()
    {
        try
        {
            var monitorData = _screenCapture.GetMonitorInfo();
            if (_protocolHandler != null)
            {
                await _protocolHandler.WriteMessageAsync(new ProtocolMessage
                {
                    Type = MessageType.MonitorInfo,
                    Data = monitorData
                }, CancellationToken.None);
            }
            Console.WriteLine($"[ClientConnection] Sent monitor info ({monitorData.Length} bytes)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ClientConnection] Error sending monitor info: {ex.Message}");
        }
    }

    private async Task HandleAuthRequest(ProtocolMessage message)
    {
        var data = message.Data ?? Array.Empty<byte>();
        if (data.Length < 1)
        {
            await SendAuthFailure();
            return;
        }

        byte authType = data[0];
        byte[]? pubKey;
        string label = "Device";
        bool isPairing = false;

        if (authType == AuthTypeKeyBegin)
        {
            // [0x10][pubKey] - authenticate an already-enrolled device.
            pubKey = data.Skip(1).ToArray();
            if (pubKey.Length == 0 || !_securityManager.IsEnrolled(pubKey))
            {
                // Not enrolled: the client treats this failure as "offer pairing".
                Console.WriteLine($">>> Unrecognized device. Enter PIN {_securityManager.PairingPin} in the app to pair. <<<");
                await SendAuthFailure();
                return;
            }
        }
        else if (authType == AuthTypePairBegin)
        {
            // [0x11][pinLen][pin][labelLen][label][pubKey] - enroll a new device.
            if (!TryParsePairBegin(data, out var pin, out label, out pubKey))
            {
                await SendAuthFailure();
                return;
            }
            if (!_securityManager.ValidatePin(pin))
            {
                await SendAuthFailure();
                return;
            }
            isPairing = true;
        }
        else
        {
            Console.WriteLine($"Unsupported authentication type: 0x{authType:X2}");
            await SendAuthFailure();
            return;
        }

        // Issue a single-use challenge; the client must sign it with its private key to prove
        // possession before we authenticate (or, for pairing, before we enroll the key).
        var nonce = SecurityManager.GenerateNonce();
        _pendingNonce = nonce;
        _pendingPubKey = pubKey;
        _pendingLabel = label;
        _pendingIsPairing = isPairing;

        if (_protocolHandler != null)
        {
            await _protocolHandler.WriteMessageAsync(new ProtocolMessage
            {
                Type = MessageType.AuthChallenge,
                Data = nonce
            }, CancellationToken.None);
        }
    }

    private static bool TryParsePairBegin(byte[] data, out string pin, out string label, out byte[] pubKey)
    {
        pin = "";
        label = "Device";
        pubKey = Array.Empty<byte>();

        int off = 1; // skip auth type byte
        if (data.Length < off + 1) return false;
        int pinLen = data[off++];
        if (pinLen <= 0 || data.Length < off + pinLen + 1) return false;
        pin = System.Text.Encoding.ASCII.GetString(data, off, pinLen);
        off += pinLen;

        int labelLen = data[off++];
        if (labelLen < 0 || data.Length < off + labelLen) return false;
        label = labelLen > 0 ? System.Text.Encoding.UTF8.GetString(data, off, labelLen) : "Device";
        off += labelLen;

        pubKey = data.Skip(off).ToArray();
        return pubKey.Length > 0;
    }

    private async Task HandleAuthResponse(ProtocolMessage message)
    {
        var signature = message.Data ?? Array.Empty<byte>();
        var nonce = _pendingNonce;
        var pubKey = _pendingPubKey;
        _pendingNonce = null; // single-use, regardless of outcome

        if (nonce == null || pubKey == null || signature.Length == 0 ||
            !_securityManager.VerifyChallenge(pubKey, nonce, signature))
        {
            Console.WriteLine("[Auth] Challenge signature verification failed.");
            _authenticated = false;
            await SendAuthFailure();
            return;
        }

        if (_pendingIsPairing)
        {
            _securityManager.EnrollDevice(pubKey, _pendingLabel);
        }
        else
        {
            _securityManager.MarkSeen(pubKey);
        }

        _authenticated = true;
        _authenticatedPubKey = pubKey;

        if (_protocolHandler != null)
        {
            await _protocolHandler.WriteMessageAsync(new ProtocolMessage
            {
                Type = MessageType.AuthSuccess
            }, CancellationToken.None);
        }

        await StartAuthenticatedSession();
    }

    private Task HandleUnpair()
    {
        // Only an authenticated client may revoke its own enrolled key.
        if (_authenticated && _authenticatedPubKey != null)
        {
            _securityManager.RemoveDevice(_authenticatedPubKey);
            _authenticatedPubKey = null;
            Console.WriteLine("[Auth] Client requested unpair; key revoked.");
        }
        return Task.CompletedTask;
    }

    private async Task SendAuthFailure()
    {
        if (_protocolHandler != null)
        {
            await _protocolHandler.WriteMessageAsync(new ProtocolMessage
            {
                Type = MessageType.AuthFailure
            }, CancellationToken.None);
        }
    }

    /// <summary>Begin streaming after a successful challenge-response authentication.</summary>
    private async Task StartAuthenticatedSession()
    {
        {
            // Take over as the single active streaming session; end any prior one ("newest wins").
            ClientConnection? previous;
            lock (_sessionLock)
            {
                previous = _activeConnection;
                _activeConnection = this;
            }
            if (previous != null && previous != this)
            {
                previous.EndSession("another device connected");
            }

            // Small delay to ensure client has set up message handlers
            await Task.Delay(100);

            // Tell the client our tier (Free/Pro) so it can gate Pro UI. Sent before MonitorInfo so
            // the app knows the tier as early as possible.
            await SendServerInfo();

            // Send monitor info so client knows available monitors
            await SendMonitorInfo();
            
            // Don't start stopwatch yet - wait until audio is initialized
            // This ensures video and audio timestamps start at the same time
            // Use stored cancellation token so these tasks respond to server shutdown
            // Wrap task launches in try-catch to log any initialization failures
            try
            {
                _ = Task.Run(async () =>
                {
                    try { await StreamScreenFrames(_cancellationToken); }
                    catch (OperationCanceledException) { } // Normal shutdown
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ClientConnection] StreamScreenFrames crashed: {ex.Message}");
                        CrashLogger.LogCrash("StreamScreenFrames", ex);
                    }
                }, _cancellationToken);
                
                _ = Task.Run(async () =>
                {
                    try { await StreamCursorPosition(_cancellationToken); }
                    catch (OperationCanceledException) { } // Normal shutdown
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ClientConnection] StreamCursorPosition crashed: {ex.Message}");
                        CrashLogger.LogCrash("StreamCursorPosition", ex);
                    }
                }, _cancellationToken);
                
                _ = Task.Run(async () =>
                {
                    try { await StreamAudioFrames(_cancellationToken); }
                    catch (OperationCanceledException) { } // Normal shutdown
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ClientConnection] StreamAudioFrames crashed: {ex.Message}");
                        CrashLogger.LogCrash("StreamAudioFrames", ex);
                    }
                }, _cancellationToken);
                
                Console.WriteLine("Authentication successful, starting screen capture and audio streams");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ClientConnection] Error launching streaming tasks: {ex.Message}");
                CrashLogger.LogCrash("Streaming Task Launch", ex);
                throw;
            }
            
            // CRITICAL: Restart encoder AFTER a delay to ensure streaming tasks have captured first frames
            // Immediate restart causes race condition where encoder restarts mid-capture, causing corruption
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500, _cancellationToken); // Wait 500ms for streams to stabilize
                    _screenCapture?.RestartEncoder();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ClientConnection] Error restarting encoder: {ex.Message}");
                    CrashLogger.LogCrash("Encoder Restart", ex);
                }
            }, _cancellationToken);
            
            Console.WriteLine($"[Gamepad] Status: {Input.InputHandler.GetGamepadStatus()}");
        }
    }

    private async Task SendAudioConfig(CancellationToken cancellationToken)
    {
        if (_protocolHandler == null) return;

        try
        {
            // Get Opus header (CSD) from audio capture service
            var opusHeader = _audioCapture?.GetOpusHeader();
            
            // Audio config format: 
            // - sample rate (4 bytes), channels (1 byte), codec type (1 byte)
            // - Opus header length (2 bytes, big-endian) if present
            // - Opus header data (variable length) if present
            var sampleRate = 48000;
            var channels = 2;
            var codecType = 1; // PCM (0 = Opus, 1 = PCM)
            
            int headerLength = opusHeader?.Length ?? 0;
            var data = new List<byte>();
            
            // Write sample rate as big-endian (network byte order)
            data.Add((byte)(sampleRate >> 24));
            data.Add((byte)(sampleRate >> 16));
            data.Add((byte)(sampleRate >> 8));
            data.Add((byte)sampleRate);
            data.Add((byte)channels);
            data.Add((byte)codecType);
            
            // Add Opus header if available
            if (opusHeader != null && opusHeader.Length > 0)
            {
                data.Add((byte)(headerLength >> 8));
                data.Add((byte)headerLength);
                data.AddRange(opusHeader);
                Console.WriteLine($"[ClientConnection] Including Opus header (CSD): {headerLength} bytes");
            }
            else
            {
                data.Add(0); // Header length = 0
                data.Add(0);
                Console.WriteLine($"[ClientConnection] No extra header needed for PCM");
            }

            var message = new ProtocolMessage
            {
                Type = MessageType.AudioConfig,
                Data = data.ToArray()
            };

            await _protocolHandler.WriteMessageAsync(message, cancellationToken);
            string codecName = (codecType == 1) ? "PCM" : "Opus";
            Console.WriteLine($"[ClientConnection] Sent audio config: {sampleRate} Hz, {channels} channels, {codecName}, Header: {headerLength} bytes");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ClientConnection] Error sending audio config: {ex.Message}");
        }
    }

    private async Task StreamAudioFrames(CancellationToken cancellationToken)
    {
        // ALWAYS force restart FFmpeg for new client connections
        // This ensures clean state and prevents issues from stale buffers/state from previous connections
        Console.WriteLine("[ClientConnection] Force restarting audio capture for new client connection...");
        try
        {
            // Force complete restart of audio capture pipeline
            _audioCapture?.ForceRestart();
            
            // Brief delay to ensure FFmpeg process is fully terminated
            await Task.Delay(100, cancellationToken);
            
        // Initialize fresh
            _audioCapture?.Initialize();
            
            // Trigger a frame capture to ensure FFmpeg is producing output
            if (_audioCapture != null)
            {
                await _audioCapture.CaptureFrameAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ClientConnection] Error during audio initialization: {ex.Message}");
        }
        
        // PCM requires no header wait
        // Send audio config immediately
        await SendAudioConfig(cancellationToken);
        
        // NOW start the stopwatch - both video and audio are ready to stream
        // This ensures timestamps start at the same time for both streams
        lock (this)
        {
            if (_streamStopwatch == null)
            {
                // Flush audio frame queue FIRST to discard frames captured during header extraction wait
                // This ensures only fresh frames (with correct timestamps) are sent
                _audioCapture?.ClearFrameQueue();
                
                // Brief pause to allow any in-flight frames from FFmpeg's internal buffers to arrive and be discarded
                // With low-latency FFmpeg flags (page_duration=2.5ms), this ensures a clean break
                System.Threading.Thread.Sleep(5); // 5ms = 2x page_duration (2.5ms) - minimized for lowest startup latency
                _audioCapture?.ClearFrameQueue(); // Clear again after the pause
                
                _streamStopwatch = System.Diagnostics.Stopwatch.StartNew();
                Console.WriteLine("[ClientConnection] Stream stopwatch started - A/V sync timestamps begin now (audio queue flushed twice with 50ms gap)");
            }
        }

        long totalAudioFrames = 0;
        long streamStartTimeUs = _streamStopwatch?.ElapsedTicks * 1_000_000L / System.Diagnostics.Stopwatch.Frequency ?? 0;

        int frameCount = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var lastFrameTime = stopwatch.ElapsedMilliseconds;
        var pendingSends = new List<Task>(); // Local list for thread safety

        while (!cancellationToken.IsCancellationRequested && _client.Connected && _authenticated)
        {
            try
            {
                // Calculate timestamp based on frame count to ensure monotonic 10ms spacing
                // This prevents "clumping" of timestamps when we read a burst of buffered frames from FFmpeg
                // which confuses the client's sync logic (jitter buffer)
                long calculatedDurationUs = totalAudioFrames * 10_000L; // 10ms per frame
                var timestampUs = streamStartTimeUs + calculatedDurationUs;
                
                // Capture audio frame
                var audioFrame = await _audioCapture!.CaptureFrameAsync(cancellationToken);

                if (audioFrame != null && audioFrame.Length > 0 && _protocolHandler != null)
                {
                    // Log frame size more frequently to debug
                    if (frameCount % 10 == 0) // Every 10 frames for better visibility
                    {
                        var elapsed = stopwatch.ElapsedMilliseconds - lastFrameTime;
                        // FPS calculation for audio is misleading due to buffering bursts, just log count/size
                        // Use totalAudioFrames to verify monotonic increase (0, 10, 20...)
                        Console.WriteLine($"Sending audio frame {frameCount} (Total: {totalAudioFrames}): {audioFrame.Length} bytes");
                        lastFrameTime = stopwatch.ElapsedMilliseconds;
                        frameCount = 0; // Reset counter for next measurement
                    }
                    
                    totalAudioFrames++; // Increment total frames for monotonic timestamp generation
                    
                    // Small Opus frames (60-100 bytes) are normal for silence or quiet audio - no warning needed

                    // Use timestamp captured before audio capture for accurate A/V sync
                    var sendTimeUs = _streamStopwatch != null ? _streamStopwatch.ElapsedTicks * 1_000_000L / System.Diagnostics.Stopwatch.Frequency : 0L;
                    
                    // Log sync info occasionally for debugging
                    if (frameCount % 100 == 0 && frameCount > 0)
                    {
                        Console.WriteLine($"[SYNC] Audio frame {frameCount}: timestamp={timestampUs / 1000}ms, sendTime={sendTimeUs / 1000}ms");
                    }
                    
                    var frameWithTimestamp = new byte[8 + audioFrame.Length];
                    // Write timestamp as big-endian (network byte order)
                    WriteBigEndianTimestamp(frameWithTimestamp, 0, timestampUs);
                    Array.Copy(audioFrame, 0, frameWithTimestamp, 8, audioFrame.Length);

                    var message = new ProtocolMessage
                    {
                        Type = MessageType.AudioFrame,
                        Data = frameWithTimestamp
                    };

                    // Pipeline: Send frame in background (fire-and-forget) to avoid blocking audio capture
                    // This reduces audio latency by ensuring capture timing is independent of network timing
                    
                    // Use a minimal buffer (1 frame = ~2.5ms) to minimize latency
                    if (pendingSends.Count > 1)
                    {
                        var finishedTask = await Task.WhenAny(pendingSends);
                        pendingSends.Remove(finishedTask);
                        try { await finishedTask; } catch { } // Consume exceptions
                    }
                    
                    var sendTask = _protocolHandler.WriteMessageAsync(message, cancellationToken);
                    pendingSends.Add(sendTask);
                    
                    // Clean up finished tasks occasionally
                    if (frameCount % 10 == 0)
                    {
                        pendingSends.RemoveAll(t => t.IsCompleted);
                    }

                    frameCount++;
                }
                else
                {
                    // No frame available, yield to allow other tasks
                    await Task.Yield();
                }
            }
            catch (System.IO.IOException ex) when (ex.Message.Contains("forcibly closed") || ex.Message.Contains("broken pipe"))
            {
                Console.WriteLine("Client disconnected (connection closed)");
                break;
            }
            catch (OperationCanceledException)
            {
                break; // Normal shutdown
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error streaming audio frame: {ex.Message}");
                CrashLogger.LogCrash("StreamAudioFrames Loop", ex);
                // Don't break on error, just log and continue
                await Task.Delay(100, cancellationToken);
            }
        }
        stopwatch.Stop();
        Console.WriteLine($"Stopped streaming audio frames. Total frames sent: {frameCount}");
    }
}
