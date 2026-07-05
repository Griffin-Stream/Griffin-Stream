using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.DXGI;
using SharpDX.Direct3D11;
using Device = SharpDX.Direct3D11.Device;

namespace PCRemote.Server.ScreenCapture;

public class ScreenCaptureService : IDisposable
{
    // Win32 APIs for cursor rendering
    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(out CURSORINFO pci);
    
    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, 
        int cxWidth, int cyHeight, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);
    
    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
    
    [DllImport("user32.dll")]
    private static extern IntPtr CopyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    /// <summary>
    /// Highest current refresh rate (Hz) across all attached displays, or 0 if it can't be read.
    /// Used to cap the requested fps: DXGI duplication can't produce frames faster than the
    /// physical panel refreshes, so asking for more just wastes pacing cycles.
    /// </summary>
    private static int GetMaxDisplayRefreshHz()
    {
        int max = 0;
        try
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            uint i = 0;
            while (EnumDisplayDevices(null, i, ref dd, 0))
            {
                if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
                {
                    var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
                    if (EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref dm) && dm.dmDisplayFrequency > 1)
                    {
                        max = Math.Max(max, (int)dm.dmDisplayFrequency);
                    }
                }
                dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
                i++;
            }
        }
        catch { }
        return max;
    }

    private const int CURSOR_SHOWING = 0x00000001;
    private const uint DI_NORMAL = 0x0003;
    
    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }
    
    private Device? _device;
    private SharpDX.Direct3D11.Device1? _device1; // Device1 interface needed for duplication - must stay alive!
    private OutputDuplication? _duplication;
    private Texture2D? _stagingTexture;
    private Output1? _output1;
    private H264Encoder? _encoder;
    private NVENCEncoder? _nvencEncoder;
    private bool _useNVENC = false;
    private bool _useSoftwareEncoder = false; // libx264/libx265 fallback when NVENC is absent
    private bool _hevcAvailable = false;
    private int _nvencRestartAttempts = 0; // bounds NVENC restart loops before demoting to software

    // Negotiated video config (from client ScreenConfig). Defaults preserve prior behavior
    // but lean toward quality (HEVC off until requested, 15 Mbps base).
    private bool _cfgUseHevc = false;
    private int _cfgBitDepth = 8;
    private int _cfgQualityMode = NVENCEncoder.QualityBalanced;
    private int _cfgFps = 120;
    private int _cfgBaseBitrateBps = 15000000; // base target before zoom boost
    private int _cfgResolutionMode = 0;        // 0=native,1=match-device,2=720,3=1080,4=1440
    private int _cfgTargetWidth = 0;
    private int _cfgTargetHeight = 0;
    private int _runtimeBitrateOverrideBps = 0; // from BitrateChange; 0 = none
    private int _currentEncoderBitrate = 0;     // bitrate the live encoder was built with
    private bool _initialized = false;
    private bool _accessLostLogged = false;
    private int _recreateAttempts = 0;
    private const int MAX_RECREATE_ATTEMPTS = 3;
    // ASYNC PIPELINE: Separate locks for capture vs encode to reduce contention
    private readonly object _captureLock = new();  // Protects Desktop Duplication resources
    private readonly object _encodeLock = new();   // Protects encoder state
    private Factory1? _factory;
    private Adapter1? _adapter;
    private Output? _output;
    private bool _useGdiFallback = false; // Use GDI when Desktop Duplication fails
    private int _gdiErrorCount = 0;
    private int _duplicationFrameCount = 0; // Track Desktop Duplication frames for logging // Counter for throttling error logs
    private int _gdiFrameCount = 0; // Counter for GDI frames captured
    private float _zoomScale = 1.0f; // Current zoom scale from client
    private float _lastEncoderZoom = 1.0f; // Last zoom level when encoder was created (for detecting mode changes)
    private int _currentEncoderWidth = 0; // Track current encoder resolution
    private int _currentEncoderHeight = 0;
    
    // Multi-monitor support
    private int _selectedMonitorIndex = 0; // 0 = primary, 1+ = secondary monitors, -1 = all monitors combined
    private int _monitorCount = 0;
    private System.Drawing.Rectangle[] _monitorBounds = Array.Empty<System.Drawing.Rectangle>();
    
    // Cursor caching to reduce per-frame overhead
    private IntPtr _lastCursorHandle = IntPtr.Zero;
    private int _cachedHotspotX = 0;
    private int _cachedHotspotY = 0;
    
    // GDI Reuse Buffers
    private Bitmap? _gdiBitmap;
    private Graphics? _gdiGraphics;
    
    private byte[]? _frameBuffer; // Reusable buffer to minimize GC pressure (moved to correct class level location)

    // Idle heartbeat: when the desktop is static, Desktop Duplication returns no new frames.
    // That leaves the client showing a black screen on connect, a 0 FPS readout, and delayed
    // monitor switches. We cache the last captured raw frame and re-encode it periodically so
    // the client keeps receiving valid frames (tiny static P-frames) even when nothing moves.
    private int _lastRawWidth;
    private int _lastRawHeight;
    private int _lastRawStride;
    private bool _hasCapturedFrame = false;
    private long _lastFrameSentMs = 0;
    private long _lastRealFrameMs = 0; // last time the desktop actually changed (real capture)
    private const long IdleHeartbeatMs = 100; // ~10 FPS floor when idle keeps the decoder fed
    private const long IdleThresholdMs = 250; // no real frame for this long => screen is idle/static

    /// <summary>
    /// True when the desktop is static (no real captured frame recently, only heartbeats).
    /// The server reports this to the client so the FPS HUD can show a stable "Idle" state
    /// instead of a jittery low number inferred from heartbeat timing.
    /// </summary>
    public bool IsScreenIdle => _hasCapturedFrame && (Environment.TickCount64 - _lastRealFrameMs) > IdleThresholdMs;
    
    // ASYNC PIPELINE: Double-buffered frame acquisition
    private readonly Texture2D?[] _stagingTextures = new Texture2D?[2];  // Ping-pong buffers
    private readonly byte[]?[] _frameBuffers = new byte[]?[2];            // Corresponding CPU buffers
    private readonly SemaphoreSlim _frameReadySemaphore = new(0, 2);     // Signal when frame ready
    private readonly CancellationTokenSource _asyncCancellation = new(); // Cancel async operations
    
    public void SetZoomScale(float scale)
    {
        _zoomScale = scale;
    }

    /// <summary>True if the local FFmpeg build exposes hevc_nvenc.</summary>
    public bool IsHevcAvailable => _hevcAvailable;

    /// <summary>
    /// Apply the client's negotiated video configuration. Forces an encoder rebuild on the
    /// next frame so the new codec/bitrate/resolution takes effect with a fresh IDR.
    /// </summary>
    public void SetVideoConfig(bool useHevc, int bitDepth, int qualityMode, int fpsCap,
                              int bitrateKbps, int resolutionMode, int targetWidth, int targetHeight)
    {
        lock (_encodeLock)
        {
            _cfgUseHevc = useHevc && _hevcAvailable;
            _cfgBitDepth = bitDepth;
            _cfgQualityMode = qualityMode;
            _cfgFps = fpsCap > 0 ? fpsCap : 120;
            // DXGI can't capture faster than the panel refreshes; cap so pacing targets a rate we
            // can actually hit (e.g. 120 requested on a 60 Hz monitor becomes 60).
            int refreshHz = GetMaxDisplayRefreshHz();
            if (refreshHz > 1 && _cfgFps > refreshHz)
            {
                Console.WriteLine($"[ScreenCapture] Requested {_cfgFps} fps exceeds display refresh; capping to {refreshHz} Hz");
                _cfgFps = refreshHz;
            }
            _cfgBaseBitrateBps = bitrateKbps > 0 ? bitrateKbps * 1000 : 15000000;
            _cfgResolutionMode = resolutionMode;
            _cfgTargetWidth = targetWidth;
            _cfgTargetHeight = targetHeight;
            _runtimeBitrateOverrideBps = 0; // explicit config resets adaptive override
            // Force rebuild on next frame
            _currentEncoderWidth = 0;
            _currentEncoderHeight = 0;
        }
        Console.WriteLine($"[ScreenCapture] Video config: codec={(_cfgUseHevc ? "HEVC" : "H264")}, {bitDepth}-bit, quality={qualityMode}, fps={_cfgFps}, bitrate={(bitrateKbps > 0 ? bitrateKbps + "kbps" : "auto")}, resMode={resolutionMode}, target={targetWidth}x{targetHeight}" + (useHevc && !_hevcAvailable ? " (HEVC requested but unavailable -> H264)" : ""));
    }

    /// <summary>
    /// The client-requested frame-rate cap (frames per second). The streaming loop uses this
    /// to pace how often it sends frames, so the negotiated fps is actually honored instead of
    /// running as fast as the capture/encode pipeline allows.
    /// </summary>
    public int TargetFps => _cfgFps;

    // Guards so adaptive changes can't thrash the encoder (each rebuild is a brief glitch).
    private DateTime _lastRuntimeBitrateApply = DateTime.MinValue;
    private const double RuntimeBitrateMinDelta = 0.20;      // ignore changes under 20%
    private static readonly TimeSpan RuntimeBitrateCooldown = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Runtime bitrate change (adaptive). Rebuilds the encoder only when the new bitrate
    /// differs meaningfully from the current one AND a cooldown has elapsed, so client-side
    /// oscillation can't cause repeated encoder restarts / visible glitches.
    /// </summary>
    public void SetRuntimeBitrate(int bitrateKbps)
    {
        if (bitrateKbps <= 0) return;
        int newBps = bitrateKbps * 1000;
        lock (_encodeLock)
        {
            int currentBps = _runtimeBitrateOverrideBps > 0 ? _runtimeBitrateOverrideBps : _cfgBaseBitrateBps;
            double delta = currentBps > 0 ? Math.Abs(newBps - currentBps) / (double)currentBps : 1.0;

            // Ignore trivial changes.
            if (delta < RuntimeBitrateMinDelta)
            {
                return;
            }

            // Rate-limit rebuilds. Always allow a significant *drop* through (responsiveness
            // when the network degrades); defer small/upward changes until the cooldown passes.
            bool isDrop = newBps < currentBps;
            bool cooledDown = (DateTime.UtcNow - _lastRuntimeBitrateApply) >= RuntimeBitrateCooldown;
            if (!cooledDown && !isDrop)
            {
                return;
            }

            _runtimeBitrateOverrideBps = newBps;
            _currentEncoderWidth = 0; // force rebuild to apply new bitrate
            _currentEncoderHeight = 0;
            _lastRuntimeBitrateApply = DateTime.UtcNow;
        }
        Console.WriteLine($"[ScreenCapture] Runtime bitrate change applied: {bitrateKbps} kbps");
    }

    /// <summary>Disable both FFmpeg encoder paths (NVENC + software) so we fall back to JPEG.</summary>
    private void DisableFFmpegEncoder()
    {
        _useNVENC = false;
        _useSoftwareEncoder = false;
    }

    /// <summary>
    /// Step down the encoder after a genuine runtime failure: NVENC -> software libx264/libx265
    /// -> JPEG. A healthy NVIDIA path never reaches here (it keeps returning valid frames), so
    /// GPU users are not demoted by normal operation - only by an encoder that actually stops
    /// producing frames. This also fixes the old behavior where an NVENC failure skipped the good
    /// software path and dropped straight to slow JPEG.
    /// </summary>
    private void DemoteEncoderAfterFailure()
    {
        _nvencEncoder?.Dispose();
        _nvencEncoder = null;
        _nvencRestartAttempts = 0;

        if (_useNVENC)
        {
            _useNVENC = false;
            _useSoftwareEncoder = DetectSoftwareEncoder();
            Console.WriteLine(_useSoftwareEncoder
                ? "[ScreenCapture] NVENC failed at runtime -> switching to software libx264/libx265."
                : "[ScreenCapture] NVENC failed and no software encoder available -> JPEG fallback.");
        }
        else
        {
            _useSoftwareEncoder = false;
            Console.WriteLine("[ScreenCapture] Software FFmpeg encoder failed -> JPEG fallback.");
        }

        // Force a fresh encoder build on the next frame.
        _currentEncoderWidth = 0;
        _currentEncoderHeight = 0;
    }

    /// <summary>Discrete target bitrate (bps): base config + zoom boost or adaptive override.</summary>
    private int ComputeTargetBitrate()
    {
        int baseBps = _runtimeBitrateOverrideBps > 0 ? _runtimeBitrateOverrideBps : _cfgBaseBitrateBps;
        // Discrete zoom boost so the encoder only rebuilds on meaningful changes.
        if (_zoomScale > 1.3f) return (int)(baseBps * 1.5);
        if (_zoomScale > 1.0f) return (int)(baseBps * 1.25);
        return baseBps;
    }

    /// <summary>Compute the encode (output) size for the requested resolution mode. Only downscales.</summary>
    private (int w, int h) ComputeEncodeSize(int inW, int inH)
    {
        int targetH = _cfgResolutionMode switch
        {
            2 => 720,
            3 => 1080,
            4 => 1440,
            _ => 0
        };

        double scale = 1.0;
        if (_cfgResolutionMode == 1 && _cfgTargetWidth > 0 && _cfgTargetHeight > 0)
        {
            // Match device: fit within the device resolution, never upscale.
            scale = Math.Min((double)_cfgTargetWidth / inW, (double)_cfgTargetHeight / inH);
        }
        else if (_cfgResolutionMode == 0 && _cfgTargetWidth > 0 && _cfgTargetHeight > 0)
        {
            // Native: allow up to ~1.5x the client's own resolution for supersampled crispness,
            // but cap there so a high-res desktop (e.g. 4K) doesn't waste bitrate sending far more
            // pixels than a phone panel can resolve. Never upscale.
            double cap = Math.Min(1.5 * _cfgTargetWidth / inW, 1.5 * _cfgTargetHeight / inH);
            if (cap < 1.0) scale = cap;
        }
        else if (targetH > 0 && inH > targetH)
        {
            scale = (double)targetH / inH;
        }

        int outW, outH;
        if (scale >= 1.0) { outW = inW; outH = inH; } // native / no upscale
        else
        {
            outW = (int)Math.Round(inW * scale);
            outH = (int)Math.Round(inH * scale);
        }

        // Software (CPU) encoders can't sustain 1080p+ in real time on typical non-NVIDIA PCs.
        // Cap the software encode to <=1080p so the CPU keeps up (smoother playback and better
        // perceived quality than a stuttering higher resolution). NVENC is untouched: this only
        // applies when the hardware path is off and the software path is active.
        if (!_useNVENC && _useSoftwareEncoder && outH > 1080)
        {
            outW = (int)Math.Round(outW * (1080.0 / outH));
            outH = 1080;
        }

        // yuv420 requires even dimensions
        outW -= outW % 2;
        outH -= outH % 2;
        if (outW < 2) outW = 2;
        if (outH < 2) outH = 2;
        return (outW, outH);
    }
    
    /// <summary>
    /// Force restart the NVENC encoder. Used when a new client connects to ensure
    /// they receive a fresh keyframe (I-frame) instead of orphaned P-frames.
    /// </summary>
    public void RestartEncoder()
    {
        lock (_encodeLock)  // Use encode lock
        {
            if (_nvencEncoder != null && (_useNVENC || _useSoftwareEncoder))
            {
                try
                {
                    Console.WriteLine("[ScreenCapture] Restarting encoder for new client connection...");
                    _nvencEncoder.ForceRestart();
                    Console.WriteLine("[ScreenCapture] Encoder restarted successfully");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ScreenCapture] Failed to restart encoder: {ex.Message}");
                    // Don't disable NVENC, just log the error - encoder may recover on next frame
                }
            }
        }
    }
    
    /// <summary>
    /// Sets the monitor to capture.
    /// </summary>
    /// <param name="monitorIndex">0 = primary, 1+ = secondary monitors, -1 = all monitors combined</param>
    public void SetSelectedMonitor(int monitorIndex)
    {
        if (monitorIndex < -1 || (monitorIndex >= _monitorCount && _monitorCount > 0))
        {
            Console.WriteLine($"[ScreenCapture] Invalid monitor index: {monitorIndex} (available: 0-{_monitorCount - 1}, or -1 for all)");
            return;
        }
        
        if (_selectedMonitorIndex != monitorIndex)
        {
            Console.WriteLine($"[ScreenCapture] Switching to monitor {monitorIndex} (was {_selectedMonitorIndex})");
            _selectedMonitorIndex = monitorIndex;
            
            // Force encoder reinit on next frame due to resolution change
            _currentEncoderWidth = 0;
            _currentEncoderHeight = 0;
            
            // Reinit Desktop Duplication for new monitor if not using GDI fallback
            if (!_useGdiFallback)
            {
                try
                {
                    lock (_captureLock)  // Use capture lock
                    {
                        _duplication?.Dispose();
                        _stagingTexture?.Dispose();
                        _stagingTexture = null;
                        _duplicationFrameCount = 0; // Reset to enable diagnostic logging on new monitor
                        
                        // Update output to selected monitor
                        _output?.Dispose();
                        _output1?.Dispose();
                        
                        int outputIndex = monitorIndex < 0 ? 0 : monitorIndex;
                        _output = _adapter?.GetOutput(outputIndex);
                        _output1 = _output?.QueryInterface<Output1>();
                        
                        CreateDuplication();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ScreenCapture] Error switching monitor: {ex.Message}");
                    _useGdiFallback = true;
                }
            }
        }
    }
    
    /// <summary>
    /// Returns info about all available monitors.
    /// Format: count (1 byte) + [index (1 byte) + width (2 bytes) + height (2 bytes) + x (2 bytes) + y (2 bytes) + isPrimary (1 byte)] per monitor
    /// </summary>
    public byte[] GetMonitorInfo()
    {
        RefreshMonitorList();
        
        var screens = System.Windows.Forms.Screen.AllScreens;
        var data = new List<byte>();
        
        // Count
        data.Add((byte)screens.Length);
        
        foreach (var screen in screens.Select((s, i) => new { Screen = s, Index = i }))
        {
            // Index
            data.Add((byte)screen.Index);
            
            // Width (big-endian)
            var width = (ushort)screen.Screen.Bounds.Width;
            data.Add((byte)(width >> 8));
            data.Add((byte)(width & 0xFF));
            
            // Height (big-endian)
            var height = (ushort)screen.Screen.Bounds.Height;
            data.Add((byte)(height >> 8));
            data.Add((byte)(height & 0xFF));
            
            // X position (big-endian, signed)
            var x = (short)screen.Screen.Bounds.X;
            data.Add((byte)(x >> 8));
            data.Add((byte)(x & 0xFF));
            
            // Y position (big-endian, signed)
            var y = (short)screen.Screen.Bounds.Y;
            data.Add((byte)(y >> 8));
            data.Add((byte)(y & 0xFF));
            
            // Is primary
            data.Add((byte)(screen.Screen.Primary ? 1 : 0));
        }
        
        Console.WriteLine($"[ScreenCapture] Monitor info: {screens.Length} monitors, selected: {_selectedMonitorIndex}");
        foreach (var screen in screens.Select((s, i) => new { Screen = s, Index = i }))
        {
            Console.WriteLine($"  Monitor {screen.Index}: {screen.Screen.Bounds.Width}x{screen.Screen.Bounds.Height} at ({screen.Screen.Bounds.X}, {screen.Screen.Bounds.Y}) {(screen.Screen.Primary ? "(primary)" : "")}");
        }
        
        return data.ToArray();
    }
    
    public int GetSelectedMonitor() => _selectedMonitorIndex;
    public int GetMonitorCount() => _monitorCount;
    
    private void RefreshMonitorList()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        _monitorCount = screens.Length;
        _monitorBounds = screens.Select(s => s.Bounds).ToArray();
    }
    
    /// <summary>
    /// Gets the top-left coordinate of the currently selected monitor (or virtual screen).
    /// Used to normalize cursor coordinates relative to the captured frame.
    /// </summary>
    public (int x, int y) GetCurrentMonitorOffset()
    {
        if (_selectedMonitorIndex == -1)
        {
            return (System.Windows.Forms.SystemInformation.VirtualScreen.X, 
                    System.Windows.Forms.SystemInformation.VirtualScreen.Y);
        }
        else if (_selectedMonitorIndex >= 0 && _selectedMonitorIndex < _monitorCount)
        {
            // Ensure bounds are populated
            if (_monitorBounds == null || _monitorBounds.Length <= _selectedMonitorIndex)
            {
                RefreshMonitorList();
            }
            
            if (_monitorBounds != null && _selectedMonitorIndex < _monitorBounds.Length)
            {
                var bound = _monitorBounds[_selectedMonitorIndex];
                return (bound.X, bound.Y);
            }
        }
        
        // Fallback
        return (0, 0);
    }

    public Task<byte[]?> CaptureFrameAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            Initialize();
        }

        // GDI capture doesn't need the same lock protection as Desktop Duplication
        // Check outside lock for better performance
        // Also force GDI for "All Monitors" (-1) since Desktop Duplication is per-output
        if (_useGdiFallback || _selectedMonitorIndex == -1)
        {
            lock (_captureLock)  // Use capture lock for GDI
            {
                return Task.FromResult(CaptureFrameGDI());
            }
        }

        lock (_captureLock)  // Use capture lock for Desktop Duplication
        {

            if (_duplication == null || _device == null) return Task.FromResult<byte[]?>(null);

            try
            {
                // Try to acquire next frame with 4ms timeout for optimal latency/CPU balance
                // STREAMING OPTIMIZATION: 4ms is half-frame time at 120 FPS (8.3ms / 2)
                // This reduces CPU spinning compared to 0ms timeout while maintaining responsiveness
                // Previous: 0ms caused excessive CPU usage from spinning
                OutputDuplicateFrameInformation frameInfo;
                SharpDX.DXGI.Resource desktopResource;
                
                // Log first frame attempt to track when error occurs
                if (_duplicationFrameCount == 0)
                {
                    Console.WriteLine($"[Frame] Attempting first frame acquisition (duplication: 0x{_duplication.NativePointer:X})...");
                }
                
                var result = _duplication.TryAcquireNextFrame(4, out frameInfo, out desktopResource);
                
                // Log success on first frame
                if (_duplicationFrameCount == 0 && result.Success)
                {
                    Console.WriteLine($"[Frame] ✓ First frame acquired successfully!");
                    _duplicationFrameCount++; // Increment to stop logging
                }
                
                // Check result code - WaitTimeout is normal, other failures return null
                if (result.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Code)
                {
                    // No new frame available - this is normal. Re-send the last frame if we've
                    // been idle long enough so the client keeps rendering (no black screen / 0 FPS).
                    return Task.FromResult(MaybeIdleHeartbeatFrame());
                }
                
                // Check for access lost - need to recreate duplication
                // 0x887A0001 = DXGI_ERROR_ACCESS_LOST
                const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0001);
                if (result.Code == SharpDX.DXGI.ResultCode.AccessLost.Code || 
                    result.Code == SharpDX.DXGI.ResultCode.AccessDenied.Code ||
                    result.Code == DXGI_ERROR_ACCESS_LOST)
                {
                    desktopResource?.Dispose();
                    
                    // Only try to recreate a few times
                    if (_recreateAttempts < MAX_RECREATE_ATTEMPTS)
                    {
                        if (!_accessLostLogged)
                        {
                            Console.WriteLine($"Desktop duplication access lost (Code: 0x{result.Code:X})");
                            Console.WriteLine("This usually means:");
                            Console.WriteLine("  1. Running over Remote Desktop (RDP) - Desktop Duplication doesn't work over RDP");
                            Console.WriteLine("  2. Another application is using desktop duplication");
                            Console.WriteLine("  3. Display configuration changed");
                            _accessLostLogged = true;
                        }
                        _recreateAttempts++;
                        RecreateDuplication();
                        // If still not initialized, stop trying
                        if (!_initialized)
                        {
                            Console.WriteLine("Desktop duplication failed to initialize. Screen capture disabled.");
                            return Task.FromResult<byte[]?>(null);
                        }
                        // Wait a bit before trying again
                        System.Threading.Thread.Sleep(100);
                    }
                    else
                    {
                        if (_recreateAttempts == MAX_RECREATE_ATTEMPTS && !_useGdiFallback)
                        {
                            Console.WriteLine($"Desktop duplication failed after {MAX_RECREATE_ATTEMPTS} attempts.");
                            Console.WriteLine("Switching to GDI-based screen capture (works over RDP).");
                            _useGdiFallback = true;
                            _initialized = true; // Keep initialized but use GDI
                            _recreateAttempts++; // Increment to prevent more messages
                        }
                    }
                    return Task.FromResult<byte[]?>(null);
                }
                
                // Check if acquisition failed or no resource
                if (result.Failure || desktopResource == null)
                {
                    desktopResource?.Dispose();
                    // Log detailed failure info on first attempt
                    if (_gdiFrameCount == 0)
                    {
                        Console.WriteLine($"[Frame] ✗ First frame acquisition FAILED!");
                        Console.WriteLine($"[Frame] Result code: 0x{result.Code:X8} ({result.Code})");
                        Console.WriteLine($"[Frame] Result.Failure: {result.Failure}");
                        Console.WriteLine($"[Frame] desktopResource is null: {desktopResource == null}");
                    }
                    // Don't spam logs for access lost - we already handled it above
                    if (result.Code != unchecked((int)0x887A0001) && _gdiFrameCount > 0)
                    {
                        Console.WriteLine($"Frame acquisition failed: {result.Code} (0x{result.Code:X})");
                    }
                    return Task.FromResult<byte[]?>(null);
                }


                // If AccumulatedFrames is 0, it means only the mouse cursor moved (no desktop image change).
                // Since we handle cursor separately and the server draws it, we can skip processing this frame entirely.
                // This saves massive resources (no texture copy, no encoding) and increases effective FPS for actual content.
                if (frameInfo.AccumulatedFrames == 0)
                {
                    desktopResource.Dispose();
                    _duplication.ReleaseFrame();
                    // Only the cursor moved (no desktop change). Keep the client fed via heartbeat.
                    return Task.FromResult(MaybeIdleHeartbeatFrame());
                }

                using (var texture = desktopResource.QueryInterface<Texture2D>())
                {
                    if (_stagingTexture == null)
                    {
                        var desc = texture.Description;
                        desc.Usage = ResourceUsage.Staging;
                        desc.CpuAccessFlags = CpuAccessFlags.Read;
                        desc.BindFlags = BindFlags.None; // CRITICAL: Staging textures must have NO bind flags (RTX 3080 requirement)
                        desc.OptionFlags = ResourceOptionFlags.None;
                        _stagingTexture = new Texture2D(_device, desc);
                        Console.WriteLine($"[Frame] Created staging texture: {desc.Width}x{desc.Height}, Format: {desc.Format}");
                    }

                    _device.ImmediateContext.CopyResource(texture, _stagingTexture);
                    _duplication.ReleaseFrame();

                    // Map and read texture data
                    var mappedResource = _device.ImmediateContext.MapSubresource(
                        _stagingTexture, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);

                    try
                    {
                        var width = _stagingTexture.Description.Width;
                        var height = _stagingTexture.Description.Height;
                        var rowPitch = mappedResource.RowPitch;
                        var dataSize = rowPitch * height;

                        // Buffer Poooooling: Reuse buffer instead of allocating 8MB every frame!
                        if (_frameBuffer == null || _frameBuffer.Length < dataSize)
                        {
                            Console.WriteLine($"[ScreenCapture] Allocating new frame buffer: {dataSize / 1024 / 1024} MB");
                            _frameBuffer = new byte[dataSize];
                        }
                        
                        unsafe
                        {
                            System.Buffer.MemoryCopy(
                                mappedResource.DataPointer.ToPointer(),
                                System.Runtime.CompilerServices.Unsafe.AsPointer(ref _frameBuffer[0]),
                                dataSize,
                                dataSize);
                        }

                        // Use shared encoding logic (enables NVENC for both paths)
                        // Remember this frame so the idle heartbeat can re-send it when the
                        // desktop goes static (prevents black screen / 0 FPS / slow monitor switch).
                        _lastRawWidth = width;
                        _lastRawHeight = height;
                        _lastRawStride = rowPitch;
                        _hasCapturedFrame = true;
                        _lastRealFrameMs = Environment.TickCount64; // real change -> not idle
                        var encodedFrame = EncodeFrameData(_frameBuffer, width, height, rowPitch);
                        if (encodedFrame != null && encodedFrame.Length > 0) _lastFrameSentMs = Environment.TickCount64;
                        return Task.FromResult(encodedFrame);
                    }
                    finally
                    {
                        _device.ImmediateContext.UnmapSubresource(_stagingTexture, 0);
                    }
                }
            }
            catch (SharpDXException ex)
            {
                // Check if it's a timeout (normal) or other error
                if (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Code)
                {
                    // Timeout is normal when no new frame - keep the client fed via heartbeat.
                    return Task.FromResult(MaybeIdleHeartbeatFrame());
                }
                
                // Check for access lost in exception
                const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0001);
                if (ex.ResultCode.Code == DXGI_ERROR_ACCESS_LOST || 
                    ex.ResultCode.Code == SharpDX.DXGI.ResultCode.AccessLost.Code)
                {
                    // Handle access lost - will be caught by the result check above on next iteration
                    return Task.FromResult<byte[]?>(null);
                }
                
                // E_INVALIDARG suggests the API call itself is wrong - Desktop Duplication not available
                if (ex.ResultCode.Code == unchecked((int)0x80070057)) // E_INVALIDARG
                {
                    if (!_useGdiFallback)
                    {
                        Console.WriteLine($"[ERROR] E_INVALIDARG exception: {ex.Message}");
                        Console.WriteLine($"[ERROR] Stack trace: {(ex.StackTrace ?? "none")}");
                        Console.WriteLine($"Desktop Duplication API not available (Code: 0x{ex.ResultCode.Code:X})");
                        Console.WriteLine("Switching to GDI-based screen capture (works over RDP and in all scenarios).");
                        _useGdiFallback = true;
                        _initialized = true; // Keep initialized but use GDI
                    }
                    return Task.FromResult(CaptureFrameGDI());
                }
                
                Console.WriteLine($"Error capturing frame: {ex.Message} (Code: 0x{ex.ResultCode.Code:X})");
                return Task.FromResult<byte[]?>(null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error capturing frame: {ex.Message}");
                Console.WriteLine($"Exception type: {ex.GetType().Name}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
                }
                return Task.FromResult<byte[]?>(null);
            }
        }
    }


    private void Initialize()
    {
        try
        {
            // Boost process priority to High for real-time screen capture performance
            // This reduces jitter and ensures we hit frame deadlines
            try 
            {
                System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.High; 
                Console.WriteLine("Process priority set to HIGH");
            }
            catch (Exception ex) 
            {
                Console.WriteLine($"Failed to set process priority: {ex.Message}"); 
            }

            // Try to detect NVENC support; fall back to software (libx264/libx265) FFmpeg
            // encoding on non-NVIDIA PCs, and only fall back to JPEG if FFmpeg is missing.
            _useNVENC = DetectNVENC();
            if (_useNVENC)
            {
                Console.WriteLine("NVENC hardware encoder detected - will use for encoding");
            }
            else
            {
                _useSoftwareEncoder = DetectSoftwareEncoder();
                if (_useSoftwareEncoder)
                {
                    Console.WriteLine($"NVENC not available - using software FFmpeg encoder (libx264{(_hevcAvailable ? " + libx265" : "")})");
                }
                else
                {
                    Console.WriteLine("NVENC and software FFmpeg encoders not available - will use JPEG encoding");
                }
            }

            // CRITICAL FIX: Create adapter FIRST, then device FROM the adapter
            // This ensures device/adapter compatibility for Desktop Duplication API
            // Previously: device created independently caused E_INVALIDARG (0x80070057)
            _factory = new Factory1();
            _adapter = _factory.GetAdapter1(0);
            
            Console.WriteLine($"[Init] GPU: {_adapter.Description.Description}");
            Console.WriteLine($"[Init] VendorID: 0x{_adapter.Description.VendorId:X4}, DeviceID: 0x{_adapter.Description.DeviceId:X4}");
            Console.WriteLine($"[Init] VRAM: {_adapter.Description.DedicatedVideoMemory / 1024 / 1024} MB");
            
            // Create device from adapter (not independently) to ensure compatibility
            _device = new Device(_adapter);
            
            _output = _adapter.GetOutput(0);
            _output1 = _output.QueryInterface<Output1>();
            Console.WriteLine($"[Init] Output: {_output.Description.DeviceName}");

            CreateDuplication();
            _initialized = true;
            Console.WriteLine("Screen capture initialized.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to initialize screen capture: {ex.Message}");
            throw;
        }
    }

    private bool DetectNVENC()
    {
        try
        {
            string? ffmpeg = FindFFmpegPath();
            if (string.IsNullOrEmpty(ffmpeg)) return false;

            // The FFmpeg build (BtbN GPL) always LISTS h264_nvenc/hevc_nvenc whether or not an
            // NVIDIA GPU is present, so "-encoders" alone is not proof of hardware. Use it only as
            // a cheap prerequisite, then run a throwaway encode to confirm the GPU + driver work.
            // Without this, a non-NVIDIA PC picks NVENC, fails at runtime, and shows a black screen.
            string encoders = RunFFmpegCapture(ffmpeg, "-hide_banner -encoders");
            bool listsH264 = encoders.Contains("h264_nvenc", StringComparison.OrdinalIgnoreCase);
            bool listsHevc = encoders.Contains("hevc_nvenc", StringComparison.OrdinalIgnoreCase);
            if (!listsH264) return false;

            if (!ProbeEncoder(ffmpeg, "h264_nvenc"))
            {
                Console.WriteLine("[ScreenCapture] h264_nvenc is in the FFmpeg build but failed a live probe (no usable NVIDIA encoder here) - using software encoder.");
                return false;
            }

            // NVENC works. Only advertise HEVC if it, too, passes a live probe: some older NVIDIA
            // cards support H.264 NVENC but not HEVC NVENC.
            _hevcAvailable = listsHevc && ProbeEncoder(ffmpeg, "hevc_nvenc");
            if (_hevcAvailable) Console.WriteLine("[ScreenCapture] hevc_nvenc live probe passed");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"NVENC detection error: {ex.Message}");
        }
        return false;
    }

    /// <summary>Run FFmpeg and return its stdout (used for capability listings like -encoders).</summary>
    private static string RunFFmpegCapture(string ffmpeg, string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return string.Empty;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return outp;
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Live-test an FFmpeg video encoder by encoding a few synthetic frames to a null sink.
    /// Returns true only when FFmpeg exits cleanly (code 0), i.e. the encoder and any required
    /// GPU/driver actually work on this machine. This is what distinguishes a real NVENC-capable
    /// NVIDIA box from one where h264_nvenc is merely compiled into the FFmpeg build.
    /// </summary>
    private static bool ProbeEncoder(string ffmpeg, string encoder)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-hide_banner -loglevel error -f lavfi -i color=c=black:s=320x240:r=15:d=1 -c:v {encoder} -pix_fmt yuv420p -f null -",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            // Drain stderr so a full pipe can't block the probe process.
            _ = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(10000))
            {
                try { p.Kill(true); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Detect whether FFmpeg exposes the software encoders libx264 (H.264) and libx265 (HEVC).
    /// Sets <see cref="_hevcAvailable"/> when libx265 is present so HEVC can still be offered
    /// to the client on non-NVIDIA machines. Returns true if libx264 is available.
    /// </summary>
    private bool DetectSoftwareEncoder()
    {
        try
        {
            string? ffmpeg = FindFFmpegPath();
            if (string.IsNullOrEmpty(ffmpeg)) return false;

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = "-hide_banner -encoders",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null) return false;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            bool x264 = output.Contains("libx264", StringComparison.OrdinalIgnoreCase);
            bool x265 = output.Contains("libx265", StringComparison.OrdinalIgnoreCase);
            if (x265)
            {
                _hevcAvailable = true; // libx265 lets us still offer HEVC without an NVIDIA GPU
                Console.WriteLine("[ScreenCapture] libx265 (software HEVC) available");
            }
            if (x264) Console.WriteLine("[ScreenCapture] libx264 (software H.264) available");
            return x264;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Software encoder detection error: {ex.Message}");
        }
        return false;
    }

    /// <summary>Locate ffmpeg.exe in the app directory or on PATH; empty if not found.</summary>
    private string FindFFmpegPath()
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var localPath = System.IO.Path.Combine(appDir, "ffmpeg.exe");
        if (System.IO.File.Exists(localPath)) return localPath;

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var path in pathEnv.Split(System.IO.Path.PathSeparator))
            {
                try
                {
                    var fullPath = System.IO.Path.Combine(path, "ffmpeg.exe");
                    if (System.IO.File.Exists(fullPath)) return fullPath;
                }
                catch { /* ignore malformed PATH entries */ }
            }
        }
        return string.Empty;
    }

    private void CreateDuplication()
    {
        if (_device == null || _output1 == null) return;
        
        try
        {
            // Dispose old duplication and device1 if they exist
            _duplication?.Dispose();
            _device1?.Dispose();
            
            // Check if we're in a remote session (RDP) - desktop duplication doesn't work in RDP
            if (System.Environment.GetEnvironmentVariable("SESSIONNAME") == "RDP-Tcp#0")
            {
                Console.WriteLine("WARNING: Running in Remote Desktop session. Desktop Duplication API may not work.");
                Console.WriteLine("Desktop Duplication requires running on the physical console, not over RDP.");
            }
            
            // CRITICAL: Create Device1 and keep it alive! The duplication needs it to remain valid.
            // Try creating Device1 directly from adapter for better compatibility
            Console.WriteLine("[CreateDuplication] Creating Device1 interface...");
            
            // Method 1: Query from existing device (original approach)
            _device1 = _device.QueryInterface<SharpDX.Direct3D11.Device1>();
            Console.WriteLine($"[CreateDuplication] Device1 created, calling DuplicateOutput...");
            
            _duplication = _output1.DuplicateOutput(_device1);
            
            // Verify duplication was created
            if (_duplication == null)
            {
                throw new InvalidOperationException("DuplicateOutput returned null");
            }
            
            Console.WriteLine($"[CreateDuplication] ✓ Desktop duplication created successfully.");
            Console.WriteLine($"[CreateDuplication] Duplication handle: 0x{_duplication.NativePointer:X}");
        }
        catch (SharpDXException ex) when (ex.ResultCode.Code == unchecked((int)0x80070057)) // E_INVALIDARG
        {
            Console.WriteLine("ERROR: Desktop Duplication failed with E_INVALIDARG (0x80070057)");
            Console.WriteLine("This usually means:");
            Console.WriteLine("  1. Device/adapter mismatch (resolved in latest code)");
            Console.WriteLine("  2. KVM switch or virtual display adapter interference");
            Console.WriteLine("  3. Outdated graphics drivers - try updating");
            Console.WriteLine("  4. Incompatible display configuration");
            Console.WriteLine("Switching to GDI-based capture...");
            _useGdiFallback = true;
            _initialized = true;
        }
        catch (SharpDXException ex) when (ex.ResultCode.Code == unchecked((int)0x80070005)) // E_ACCESSDENIED
        {
            // LOCK SCREEN / SECURE DESKTOP HANDLING
            Console.WriteLine("WARNING: Desktop Duplication failed with E_ACCESSDENIED (0x80070005)");
            Console.WriteLine("This usually means the PC is LOCKED or on the Secure Desktop (UAC).");
            Console.WriteLine("Switching to GDI-based capture (Basic Mode)...");
            Console.WriteLine("Will attempt to restore High-Performance mode automatically when unlocked.");
            
            _useGdiFallback = true;
            _initialized = true;
        }
        catch (SharpDXException ex) when (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.NotCurrentlyAvailable.Code)
        {
            Console.WriteLine("ERROR: Desktop duplication not available. This usually means:");
            Console.WriteLine("  1. Running over Remote Desktop (RDP) - not supported");
            Console.WriteLine("  2. Another application is using desktop duplication");
            Console.WriteLine("  3. Display configuration issue");
            throw new InvalidOperationException("Desktop duplication not available", ex);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create duplication: {ex.Message}");
            Console.WriteLine($"Exception type: {ex.GetType().Name}");
            if (ex is SharpDXException sharpEx)
            {
                Console.WriteLine($"Result code: 0x{sharpEx.ResultCode.Code:X8}");
            }
            throw;
        }
    }

    private void RecreateDuplication()
    {
        lock (_captureLock)  // Use capture lock
        {
            try
            {
                _duplication?.Dispose();
                _duplication = null;
                
                // Small delay to let the system release resources
                System.Threading.Thread.Sleep(100);
                
                // Check if we're in RDP
                var sessionName = System.Environment.GetEnvironmentVariable("SESSIONNAME");
                if (sessionName?.StartsWith("RDP") == true)
                {
                    if (_recreateAttempts == 0)
                    {
                        Console.WriteLine($"Running in {sessionName} session. Desktop Duplication API does not work over Remote Desktop.");
                        Console.WriteLine("Switching to GDI-based screen capture.");
                    }
                    _useGdiFallback = true;
                    _initialized = true;
                    return;
                }
                
                CreateDuplication();
                
                // CRITICAL: Restart encoder after duplication recreation to ensure fresh keyframe
                // This handles resolution changes (e.g., tabbing out of fullscreen game)
                // Without this, encoder continues with old resolution, causing corruption
                Console.WriteLine("[ScreenCapture] Desktop Duplication recreated - restarting encoder for fresh keyframe");
                RestartEncoder();
            }
            catch (Exception ex)
            {
                if (_recreateAttempts == 0)
                {
                    Console.WriteLine($"Failed to recreate duplication: {ex.Message}");
                }
                _initialized = false; // Mark as uninitialized so it will retry on next call
            }
        }
    }

    private DateTime _lastRecoveryAttempt = DateTime.MinValue;

    private byte[]? CaptureFrameGDI()
    {
        try
        {
            // AUTO-RECOVERY: Attempt to switch back to Desktop Duplication periodically
            // We use TIME based check (every 1s) because GDI capture on Lock Screen might operate at very low FPS
            // Frame-based counting (e.g. % 120) could take forever if FPS drops to 1-5 FPS.
            if ((DateTime.UtcNow - _lastRecoveryAttempt).TotalSeconds >= 1.0)
            {
                _lastRecoveryAttempt = DateTime.UtcNow;
                
                var sessionName = System.Environment.GetEnvironmentVariable("SESSIONNAME");
                bool isRdp = sessionName?.StartsWith("RDP") == true;
                
                if (!isRdp)
                {
                     // Try to re-initialize Desktop Duplication quietly
                     try 
                     {
                         // Temporary lock to check if we can switch back
                         if (_device != null && _output1 != null)
                         {
                             // Dispose old duplication if partial
                             _duplication?.Dispose();
                             
                             // Try to create it
                             _device1 = _device.QueryInterface<SharpDX.Direct3D11.Device1>();
                             _duplication = _output1.DuplicateOutput(_device1);
                             
                             if (_duplication != null)
                             {
                                 Console.WriteLine("[Auto-Recovery] ✓ Desktop Duplication restored! Switching back to High-Performance mode.");
                                 _useGdiFallback = false;
                                 _recreateAttempts = 0; // Reset counters
                                 // Restart encoder for fresh stream
                                 RestartEncoder();
                                 
                                 // Return null for this frame to force a clean switch next frame
                                 return null;
                             }
                         }
                     }
                     catch 
                     {
                         // Still locked or unavailable - silent fail, continue with GDI
                         // AccessDenied (0x80070005) will land here
                     }
                }
            }

            // Get screen dimensions based on selected monitor
            RefreshMonitorList();
            
            int captureX, captureY, width, height;
            
            if (_selectedMonitorIndex == -1)
            {
                // Capture all monitors combined (virtual screen)
                captureX = System.Windows.Forms.SystemInformation.VirtualScreen.X;
                captureY = System.Windows.Forms.SystemInformation.VirtualScreen.Y;
                width = System.Windows.Forms.SystemInformation.VirtualScreen.Width;
                height = System.Windows.Forms.SystemInformation.VirtualScreen.Height;
            }
            else if (_selectedMonitorIndex >= 0 && _selectedMonitorIndex < _monitorCount)
            {
                // Capture specific monitor
                var bounds = _monitorBounds[_selectedMonitorIndex];
                captureX = bounds.X;
                captureY = bounds.Y;
                width = bounds.Width;
                height = bounds.Height;
            }
            else
            {
                // Fallback to primary screen
                captureX = 0;
                captureY = 0;
                width = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width ?? 1920;
                height = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Height ?? 1080;
            }

            if (width <= 0 || height <= 0)
            {
                Console.WriteLine($"WARNING: Invalid screen dimensions: {width}x{height}");
                return null;
            }

            // Always capture at native resolution for maximum performance and quality
            // Software upscaling causes blur - let the client handle zoom scaling
            // Always use PNG lossless (perfect quality) for best quality and performance
            // PNG lossless provides perfect quality with no compression artifacts or blur
            // PNG lossless provides perfect quality with no compression artifacts or blur
            
            // Create bitmap to capture screen at native resolution (always)
            // Use optimal pixel format and graphics settings for fastest capture
            // Reuse bitmap if dimensions match
            if (_gdiBitmap == null || _gdiBitmap.Width != width || _gdiBitmap.Height != height)
            {
                _gdiGraphics?.Dispose();
                _gdiBitmap?.Dispose();
                
                // Create new bitmap and graphics context
                _gdiBitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                _gdiGraphics = Graphics.FromImage(_gdiBitmap);
                
                // Optimize graphics settings for fastest capture (only need to set once)
                _gdiGraphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                _gdiGraphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                _gdiGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                _gdiGraphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
                _gdiGraphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                
                Console.WriteLine($"[ScreenCapture] Allocated new GDI bitmap: {width}x{height}");
            }
            
            // Capture from the selected monitor position
            _gdiGraphics!.CopyFromScreen(captureX, captureY, 0, 0, new System.Drawing.Size(width, height));
            
            // Cursor is now drawn client-side for zero latency
            // Server sends cursor position separately, client draws overlay instantly
                
                // Skip pixel verification - GetPixel() is slow and hurts FPS
                // Bitmap capture will either succeed or fail, no need to verify pixels

            // Convert bitmap to byte array (BGR format)
            var bitmapData = _gdiBitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format24bppRgb);

                try
                {
                    int stride = bitmapData.Stride;
                    int dataSize = stride * height;
                    
                    // Buffer Pooling: Reuse buffer
                    if (_frameBuffer == null || _frameBuffer.Length < dataSize)
                    {
                        Console.WriteLine($"[ScreenCapture] Allocating new GDI frame buffer: {dataSize / 1024 / 1024} MB");
                        _frameBuffer = new byte[dataSize];
                    }
                    
                    unsafe
                    {
                        // Copy bitmap data to frame buffer
                        System.Buffer.MemoryCopy(
                            bitmapData.Scan0.ToPointer(), 
                            System.Runtime.CompilerServices.Unsafe.AsPointer(ref _frameBuffer[0]),
                            dataSize, 
                            dataSize);
                    }
                    
                    // Unlock bits immediately after copy
                    _gdiBitmap.UnlockBits(bitmapData);
                    
                    // Use shared encoding logic
                    return EncodeFrameData(_frameBuffer, width, height, stride);
                }
                catch (Exception)
                {
                     // Ensure unlock if exception occurred
                     try { _gdiBitmap?.UnlockBits(bitmapData); } catch {}
                     throw;
                }
        }
        catch (Exception ex)
        {
            // Throttle error logs
            _gdiErrorCount++;
            if (_gdiErrorCount % 60 == 0)
            {
                Console.WriteLine($"GDI capture failed: {ex.Message}");
            }
            return null;
        }
    }

    /// <summary>
    /// When the desktop is static (no new frame from Desktop Duplication), re-encode the last
    /// captured frame if the idle interval has elapsed. Returns null if we haven't captured a
    /// frame yet or the interval hasn't passed. This keeps the client's decoder fed so the
    /// video shows immediately on connect, the FPS readout never sits at 0, and monitor
    /// switches take effect right away instead of waiting for on-screen movement.
    /// </summary>
    private byte[]? MaybeIdleHeartbeatFrame()
    {
        if (!_hasCapturedFrame || _frameBuffer == null) return null;
        long now = Environment.TickCount64;
        if (now - _lastFrameSentMs < IdleHeartbeatMs) return null;
        var encoded = EncodeFrameData(_frameBuffer, _lastRawWidth, _lastRawHeight, _lastRawStride);
        if (encoded != null && encoded.Length > 0) _lastFrameSentMs = now;
        return encoded;
    }

    private byte[]? EncodeFrameData(byte[] frameData, int width, int height, int stride)
    {
        // Try the FFmpeg encoder first if available: NVENC (hardware) when present, otherwise
        // libx264/libx265 (software). Only fall through to JPEG when neither is available.
        if (_useNVENC || _useSoftwareEncoder)
        {
            bool useSoftware = !_useNVENC;
            lock (_encodeLock)  // Use encode lock (separate from capture)
            {
            // Recreate encoder when the output resolution or target bitrate changes.
            // Output size depends on the negotiated resolution mode; bitrate on config + zoom.
            var (encW, encH) = ComputeEncodeSize(width, height);
            int targetBitrate = ComputeTargetBitrate();
            bool needsReinit = _nvencEncoder == null ||
                               _currentEncoderWidth != encW ||
                               _currentEncoderHeight != encH ||
                               _currentEncoderBitrate != targetBitrate;
            
            if (needsReinit)
            {
                Console.WriteLine($"[ScreenCapture] Encoder reinit: capture {width}x{height} -> encode {encW}x{encH}, {targetBitrate / 1000000} Mbps, zoom {_zoomScale:F2}x");
                
                // Dispose old encoder completely and wait for FFmpeg to fully terminate
                if (_nvencEncoder != null)
                {
                    _nvencEncoder.Dispose();
                    _nvencEncoder = null;
                    // Small delay to ensure FFmpeg process is fully terminated
                    Thread.Sleep(100);
                }
                
                try
                {
                    _nvencEncoder = new NVENCEncoder(
                        width, height,
                        bitrate: targetBitrate,
                        fps: _cfgFps,
                        // Software path forces H.264: libx265 is far too CPU-heavy for real time.
                        // NVENC HEVC is unaffected (useSoftware is false on the hardware path).
                        useHevc: _cfgUseHevc && !useSoftware,
                        bitDepth: _cfgBitDepth,
                        qualityMode: _cfgQualityMode,
                        outputWidth: encW,
                        outputHeight: encH,
                        useSoftware: useSoftware);
                    _nvencEncoder.Initialize();
                    _currentEncoderWidth = encW;
                    _currentEncoderHeight = encH;
                    _currentEncoderBitrate = targetBitrate;
                    _lastEncoderZoom = _zoomScale;
                    
                    Console.WriteLine($"[ScreenCapture] {(useSoftware ? "Software" : "NVENC")} encoder initialized: {(_cfgUseHevc ? "HEVC" : "H264")} {encW}x{encH} @ {_cfgFps} FPS, {targetBitrate / 1000000} Mbps, zoom: {_zoomScale:F2}x");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ScreenCapture] Encoder initialization failed: {ex.Message}");
                    DemoteEncoderAfterFailure();
                }
            }

            if (_nvencEncoder != null)
            {
                // Check if encoder is healthy before using it. Bound the restart loop so a machine
                // that can never produce frames (e.g. NVENC that passed the probe but then broke)
                // demotes to software/JPEG within a couple of seconds instead of staying black.
                if (!_nvencEncoder.IsHealthy())
                {
                    _nvencRestartAttempts++;
                    if (_nvencRestartAttempts > 2)
                    {
                        Console.WriteLine("[ScreenCapture] Encoder still unhealthy after repeated restarts - demoting.");
                        DemoteEncoderAfterFailure();
                    }
                    else
                    {
                        Console.WriteLine($"[ScreenCapture] NVENC encoder unhealthy, attempting restart ({_nvencRestartAttempts})...");
                        try
                        {
                            _nvencEncoder.ForceRestart();
                            Console.WriteLine("[ScreenCapture] NVENC encoder restarted successfully");
                        }
                        catch (Exception restartEx)
                        {
                            Console.WriteLine($"[ScreenCapture] Encoder restart failed: {restartEx.Message}");
                            DemoteEncoderAfterFailure();
                        }
                    }
                }
                
                if (_nvencEncoder != null)
                {
                    try
                    {
                        // EncodeFrame already handles async delivery and waits for the frame
                        var nvencEncoded = _nvencEncoder.EncodeFrame(frameData, width, height, stride);
                        
                        // Check for valid frame OR intentional skip (empty array)
                        if (nvencEncoded != null)
                        {
                            if (nvencEncoded.Length == 0)
                            {
                                // Intentional skip (latency reduction or timeout) - return empty to skip frame
                                return nvencEncoded; 
                            }
                            
                            if (nvencEncoded.Length >= 32) // Reduced threshold to 32 bytes to accept tiny P-frames
                            {
                                // A valid frame means the encoder is healthy; clear the failure bound
                                // so a working GPU is never demoted by transient earlier hiccups.
                                _nvencRestartAttempts = 0;
                                return nvencEncoded;
                            }
                        }
                        
                        // If NVENC returns null (first few frames or encoding delay), fall through to JPEG/PNG
                        // Log occasionally to track NVENC null frames
                        if (_gdiFrameCount % 60 == 0)
                        {
                            Console.WriteLine($"[ScreenCapture] NVENC returned null, falling back to PNG/JPEG (frame #{_gdiFrameCount}, zoom: {_zoomScale:F2}x)");
                        }
                    }
                    catch (Exception ex)
                    {
                        // NVENC failed (pipe closed, etc.) - try to restart once
                        Console.WriteLine($"[ScreenCapture] NVENC encoding failed: {ex.Message}");
                        try
                        {
                            _nvencEncoder.ForceRestart();
                            Console.WriteLine("[ScreenCapture] NVENC encoder restarted after error");
                        }
                        catch
                        {
                            DemoteEncoderAfterFailure();
                        }
                    }
                }
            }
            else
            {
                // Log when _nvencEncoder is null after init block
                if (_gdiFrameCount % 60 == 0)
                {
                    Console.WriteLine($"[ScreenCapture] _nvencEncoder is null, using fallback (frame #{_gdiFrameCount})");
                }
            }
            }  // End of encodeLock scope
        }

        // Fallback to Software Encoding (JPEG/H264). Honor the negotiated fps/bitrate instead of
        // hardcoded values so the fallback path respects the client's settings too.
        if (_encoder == null)
        {
            _encoder = new H264Encoder(width, height, bitrate: ComputeTargetBitrate(), fps: _cfgFps);
            _encoder.Initialize();
        }
        return _encoder.EncodeFrame(frameData, width, height, stride);
    }

    /// <summary>
    /// Draws the native Windows cursor onto the graphics context.
    /// This renders the actual system cursor (arrow, hand, text beam, etc.) at its current position.
    /// OPTIMIZED: Caches cursor hotspot to avoid GetIconInfo/DeleteObject overhead per frame.
    /// </summary>
    private void DrawCursorOnGraphics(Graphics graphics)
    {
        try
        {
            CURSORINFO cursorInfo = new CURSORINFO();
            cursorInfo.cbSize = Marshal.SizeOf(cursorInfo);
            
            if (GetCursorInfo(out cursorInfo) && (cursorInfo.flags & CURSOR_SHOWING) != 0)
            {
                // Cache cursor hotspot - only call GetIconInfo when cursor changes
                // This avoids expensive GDI object allocation/deallocation per frame
                if (cursorInfo.hCursor != _lastCursorHandle)
                {
                    if (GetIconInfo(cursorInfo.hCursor, out ICONINFO iconInfo))
                    {
                        _cachedHotspotX = iconInfo.xHotspot;
                        _cachedHotspotY = iconInfo.yHotspot;
                        _lastCursorHandle = cursorInfo.hCursor;
                        
                        // Clean up GDI objects from ICONINFO
                        if (iconInfo.hbmMask != IntPtr.Zero)
                            DeleteObject(iconInfo.hbmMask);
                        if (iconInfo.hbmColor != IntPtr.Zero)
                            DeleteObject(iconInfo.hbmColor);
                    }
                }
                
                // Calculate draw position using cached hotspot
                int drawX = cursorInfo.ptScreenPos.x - _cachedHotspotX;
                int drawY = cursorInfo.ptScreenPos.y - _cachedHotspotY;
                
                // Get the device context from graphics and draw cursor
                IntPtr hdc = graphics.GetHdc();
                try
                {
                    // Draw the cursor icon at the calculated position
                    DrawIconEx(hdc, drawX, drawY, cursorInfo.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }
        }
        catch (Exception ex)
        {
            // Don't let cursor drawing failures affect screen capture
            // Just skip cursor for this frame
            if (_gdiFrameCount <= 5)
            {
                Console.WriteLine($"[ScreenCapture] Cursor draw warning: {ex.Message}");
            }
        }
    }
    
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    public void Dispose()
    {
        // Cancel async operations
        _asyncCancellation?.Cancel();
        
        lock (_captureLock)
        {
            lock (_encodeLock)
            {
                _encoder?.Dispose();
                _nvencEncoder?.Dispose();
                _stagingTexture?.Dispose();
                
                // Dispose async pipeline buffers
                foreach (var tex in _stagingTextures)
                {
                    tex?.Dispose();
                }
                
                _duplication?.Dispose();
                _device1?.Dispose(); // Dispose Device1 interface
                _output1?.Dispose();
                _output?.Dispose();
                _adapter?.Dispose();
                _factory?.Dispose();
                _device?.Dispose();
                
                // Dispose GDI resources
                _gdiGraphics?.Dispose();
                _gdiBitmap?.Dispose();
            }
        }
        
        _frameReadySemaphore?.Dispose();
        _asyncCancellation?.Dispose();
    }
}
