using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Buffers;

namespace PCRemote.Server.ScreenCapture;

/// <summary>
/// Hardware-accelerated H.264 encoder using FFmpeg with NVENC
/// Requires FFmpeg with NVENC support and an NVIDIA GPU
/// </summary>
public class NVENCEncoder : IDisposable
{
    // Quality modes (mirror ScreenConfigData)
    public const int QualityPerformance = 0;
    public const int QualityBalanced = 1;
    public const int QualityHigh = 2;
    public const int QualityMax = 3;

    private readonly int _width;
    private readonly int _height;
    private readonly int _outputWidth;  // encode/output size (may downscale input)
    private readonly int _outputHeight;
    private readonly int _bitrate;
    private readonly int _fps;
    private readonly bool _useHevc;
    private readonly int _bitDepth;     // 8 or 10
    private readonly int _qualityMode;  // QualityPerformance..QualityMax
    private readonly bool _useSoftware; // true = libx264/libx265 (CPU), false = NVENC (GPU)
    private readonly string _ffmpegPath;

    public bool IsHevc => _useHevc;
    private Process? _ffmpegProcess;
    private Stream? _inputStream;
    private Stream? _outputStream;
    private bool _initialized = false;
    private readonly object _lock = new();
    private Thread? _readThread;
    private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _encodedFrames = new();
    private readonly List<byte> _pendingHeaders = new(); // Buffer for SPS/PPS/SEI to combine with video frames
    private bool _disposed = false;
    private int _consecutiveNullFrames = 0;
    private int _framesProcessed = 0; // Track frames processed since init for warmup logic
    private const int MAX_CONSECUTIVE_NULL_FRAMES = 60; // After 60 null frames (~500ms), consider encoder broken
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared; // Buffer pooling to reduce GC pressure

    public NVENCEncoder(int width, int height, int bitrate = 10000000, int fps = 60,
                        bool useHevc = false, int bitDepth = 8, int qualityMode = QualityBalanced,
                        int outputWidth = 0, int outputHeight = 0, bool useSoftware = false)
    {
        _width = width;
        _height = height;
        _outputWidth = outputWidth > 0 ? outputWidth : width;
        _outputHeight = outputHeight > 0 ? outputHeight : height;
        _bitrate = bitrate;
        _fps = fps;
        _useHevc = useHevc;
        _useSoftware = useSoftware;
        // 10-bit is only safe with HEVC (Main10); mobile H.264 High10 decode is rare.
        // Software encoding is CPU-bound, so keep it at 8-bit to protect frame rate.
        _bitDepth = (bitDepth == 10 && useHevc && !useSoftware) ? 10 : 8;
        _qualityMode = qualityMode;
        
        // Try to find FFmpeg
        _ffmpegPath = FindFFmpeg();
        if (string.IsNullOrEmpty(_ffmpegPath))
        {
            Console.WriteLine("WARNING: FFmpeg not found. NVENC encoding will not work.");
            Console.WriteLine("Please install FFmpeg with NVENC support and ensure it's in PATH or in the application directory.");
        }
    }

    private string FindFFmpeg()
    {
        // Check if ffmpeg.exe is in the application directory
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var localPath = Path.Combine(appDir, "ffmpeg.exe");
        if (File.Exists(localPath))
        {
            return localPath;
        }

        // Check PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var path in pathEnv.Split(Path.PathSeparator))
            {
                var fullPath = Path.Combine(path, "ffmpeg.exe");
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return string.Empty;
    }

    public void Initialize()
    {
        if (_initialized) return;
        
        if (string.IsNullOrEmpty(_ffmpegPath))
        {
            throw new InvalidOperationException("FFmpeg not found. Cannot initialize NVENC encoder.");
        }

        lock (_lock)
        {
            try
            {
                // Start FFmpeg process with NVENC encoding
                var startInfo = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = BuildFFmpegArguments(),
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                _ffmpegProcess = Process.Start(startInfo);
                if (_ffmpegProcess == null)
                {
                    throw new InvalidOperationException("Failed to start FFmpeg process");
                }

                _inputStream = _ffmpegProcess.StandardInput.BaseStream;
                _outputStream = _ffmpegProcess.StandardOutput.BaseStream;

                // Start thread to read encoded frames
                _readThread = new Thread(ReadEncodedFrames)
                {
                    IsBackground = true,
                    Name = "NVENCReader",
                    Priority = ThreadPriority.AboveNormal  // STREAMING: High priority for low latency
                };
                _readThread.Start();

                // Read stderr in background to catch errors
                _ffmpegProcess.BeginErrorReadLine();
                var errorBuffer = new System.Text.StringBuilder();
                _ffmpegProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        errorBuffer.AppendLine(e.Data);
                        if (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase) || 
                            e.Data.Contains("Error", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"FFmpeg error: {e.Data}");
                        }
                    }
                };

                _initialized = true;
                _framesProcessed = 0; // Reset frame counter
                string backend = _useSoftware ? (_useHevc ? "libx265 (SW)" : "libx264 (SW)") : (_useHevc ? "HEVC (NVENC)" : "H.264 (NVENC)");
                Console.WriteLine($"[Encoder] Initialized: {backend} {_bitDepth}-bit {_width}x{_height} @ {_fps} FPS, {_bitrate / 1000000} Mbps, qualityMode={_qualityMode}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NVENC] Failed to initialize encoder: {ex.Message}");
                Console.WriteLine("[NVENC] Will be disabled. Falling back to JPEG encoding.");
                Logging.CrashLogger.LogCrash("NVENC Initialization", ex);
                Dispose();
                throw;
            }
            
            // Wait a moment and check if FFmpeg process is still running
            Thread.Sleep(100);
            if (_ffmpegProcess != null && _ffmpegProcess.HasExited)
            {
                var exitCode = _ffmpegProcess.ExitCode;
                Console.WriteLine($"FFmpeg process exited immediately with code {exitCode}. NVENC initialization failed.");
                Dispose();
                throw new InvalidOperationException($"FFmpeg exited with code {exitCode}");
            }
        }
    }

    private string BuildFFmpegArguments()
    {
        if (_useSoftware)
        {
            return BuildSoftwareFFmpegArguments();
        }

        // FFmpeg command for NVENC encoding.
        // Input: raw BGRA video from stdin (NVENC handles GPU color conversion natively).
        // Output: H.264 or HEVC NAL units (Annex B) to stdout.
        var bitrateKbps = _bitrate / 1000; // Convert to kbps for FFmpeg

        string encoder = _useHevc ? "hevc_nvenc" : "h264_nvenc";
        string outputFormat = _useHevc ? "hevc" : "h264";

        // Pixel format + profile. 10-bit only applies to HEVC (Main10).
        string pixFmt;
        string profile;
        if (_useHevc)
        {
            if (_bitDepth == 10)
            {
                pixFmt = "p010le";
                profile = "main10";
            }
            else
            {
                pixFmt = "yuv420p";
                profile = "main";
            }
        }
        else
        {
            pixFmt = "yuv420p";
            profile = "high"; // High profile is compatible with mobile H.264 decoders
        }

        // Common low-latency flags for all modes:
        // -delay 0: Output frames immediately (no encoder delay)
        // -zerolatency 1: Enable zero-latency mode
        // -bf 0: No B-frames (B-frames require future frames + decoder reordering, adding delay)
        // -rc-lookahead 0: No lookahead (lookahead buffers frames, adding delay)
        // -flush_packets 1: Flush output after each packet (critical for pipe output!)
        // -forced-idr 1: Force IDR frames for instant stream start/recovery
        var zeroLatencyFlags = "-delay 0 -zerolatency 1 -bf 0 -rc-lookahead 0 -flush_packets 1 -forced-idr 1";

        // Long GOP (~5s). The transport is reliable/in-order (TCP), so periodic keyframes
        // aren't needed for loss recovery -- they only waste bitrate re-sending full frames.
        // A long GOP puts those bits into actual detail. -forced-idr still lets us emit an
        // IDR on demand (stream start, resolution/codec change).
        int gopSize = Math.Max(_fps * 5, 60);

        // Quality mode -> preset / CQ / VBV buffer / AQ. Lower CQ = higher quality.
        // HEVC CQ values run a few points higher than H.264 for equivalent quality.
        // aqStrength steers bits toward high-detail regions (text/UI edges); higher for
        // the quality-first modes where crispness matters more than absolute latency.
        string preset;
        string tune;
        int cq;
        int bufDivisor; // bufsize = bitrate / bufDivisor (smaller = lower latency)
        int aqStrength;
        switch (_qualityMode)
        {
            case QualityMax:
                preset = "p6"; tune = "hq";
                cq = _useHevc ? 16 : 12;
                bufDivisor = 1; // 1s VBV: lets quality spikes through for crisp detail
                aqStrength = 12;
                break;
            case QualityHigh:
                preset = "p5"; tune = "ll";
                cq = _useHevc ? 19 : 14;
                bufDivisor = 2;
                aqStrength = 12;
                break;
            case QualityPerformance:
                preset = "p1"; tune = "ull";
                cq = _useHevc ? 26 : 20;
                bufDivisor = 8; // minimal buffer for lowest latency
                aqStrength = 8;
                break;
            case QualityBalanced:
            default:
                preset = "p3"; tune = "ll";
                cq = _useHevc ? 22 : 16;
                bufDivisor = 4;
                aqStrength = 8;
                break;
        }

        int bufKbps = Math.Max(bitrateKbps / bufDivisor, 1000);

        // Optional downscale to the requested output resolution (only when smaller than capture).
        // Lanczos keeps text/UI edges sharper than bicubic when scaling down for the phone.
        string scaleFilter = (_outputWidth != _width || _outputHeight != _height)
            ? $"-vf scale={_outputWidth}:{_outputHeight}:flags=lanczos "
            : string.Empty;

        // Color signaling: the desktop is full-range RGB. Tagging the stream as full-range
        // BT.709 (instead of the default limited range) preserves true blacks/whites so the
        // image isn't washed out, and gives the decoder correct color primaries.
        string colorFlags = "-color_range pc -colorspace bt709 -color_primaries bt709 -color_trc bt709";

        return $"-f rawvideo -vcodec rawvideo -s {_width}x{_height} -pix_fmt bgra -framerate {_fps} -i pipe:0 " +
               scaleFilter +
               $"-c:v {encoder} " +
               $"-profile:v {profile} " +
               $"-pix_fmt {pixFmt} " +
               $"-preset {preset} " +
               $"-tune {tune} " +
               $"-rc vbr " +
               $"-cq {cq} " +
               $"-b:v {bitrateKbps}k " +
               $"-maxrate {bitrateKbps * 2}k " +
               $"-bufsize {bufKbps}k " +
               $"-g {gopSize} " +
               $"-spatial-aq 1 " +
               $"-temporal-aq 1 " +
               $"-aq-strength {aqStrength} " +
               $"{colorFlags} " +
               $"{zeroLatencyFlags} " +
               $"-f {outputFormat} " +
               $"-an " +
               $"pipe:1";
    }

    /// <summary>
    /// Build FFmpeg args for CPU (software) encoding via libx264/libx265. Used on PCs without
    /// an NVENC-capable NVIDIA GPU. Produces the same Annex-B NAL output as the NVENC path, so
    /// the read/parse pipeline is identical. Presets favor speed since software 1080p is heavy.
    /// </summary>
    private string BuildSoftwareFFmpegArguments()
    {
        var bitrateKbps = _bitrate / 1000;
        string encoder = _useHevc ? "libx265" : "libx264";
        string outputFormat = _useHevc ? "hevc" : "h264";
        // Software stays 8-bit for CPU sanity; 4:2:0 for broad mobile decode compatibility.
        string pixFmt = "yuv420p";
        string profile = _useHevc ? "main" : "high";

        // GOP mirrors the NVENC path (~5s). Long GOP is fine on reliable TCP.
        int gopSize = Math.Max(_fps * 5, 60);

        // Map quality mode -> x264/x265 speed preset + CRF. Faster presets keep real-time
        // throughput on CPUs; lower CRF = higher quality. x265 CRF runs a few points higher.
        string speed;
        int crf;
        int bufDivisor;
        switch (_qualityMode)
        {
            case QualityMax:
                speed = "veryfast"; crf = _useHevc ? 20 : 18; bufDivisor = 1; break;
            case QualityHigh:
                speed = "veryfast"; crf = _useHevc ? 23 : 20; bufDivisor = 2; break;
            case QualityPerformance:
                speed = "ultrafast"; crf = _useHevc ? 28 : 26; bufDivisor = 8; break;
            case QualityBalanced:
            default:
                speed = "superfast"; crf = _useHevc ? 26 : 23; bufDivisor = 4; break;
        }

        int bufKbps = Math.Max(bitrateKbps / bufDivisor, 1000);

        string scaleFilter = (_outputWidth != _width || _outputHeight != _height)
            ? $"-vf scale={_outputWidth}:{_outputHeight}:flags=lanczos "
            : string.Empty;

        string colorFlags = "-color_range pc -colorspace bt709 -color_primaries bt709 -color_trc bt709";

        // -tune zerolatency disables B-frames + lookahead for low latency. For x265 we also
        // quiet its verbose stderr so it doesn't flood the console.
        string codecSpecific = _useHevc
            ? $"-tune zerolatency -x265-params \"log-level=error:keyint={gopSize}:min-keyint={gopSize}:scenecut=0\" "
            : $"-tune zerolatency -x264-params \"keyint={gopSize}:min-keyint={gopSize}:scenecut=0\" ";

        return $"-f rawvideo -vcodec rawvideo -s {_width}x{_height} -pix_fmt bgra -framerate {_fps} -i pipe:0 " +
               scaleFilter +
               $"-c:v {encoder} " +
               $"-profile:v {profile} " +
               $"-pix_fmt {pixFmt} " +
               $"-preset {speed} " +
               codecSpecific +
               $"-crf {crf} " +
               $"-maxrate {bitrateKbps}k " +
               $"-bufsize {bufKbps}k " +
               $"-g {gopSize} " +
               $"-forced-idr 1 " +
               $"-flush_packets 1 " +
               $"{colorFlags} " +
               $"-f {outputFormat} " +
               $"-an " +
               $"pipe:1";
    }

    public byte[]? EncodeFrame(byte[] rawFrame, int width, int height, int stride)
    {
        if (!_initialized)
        {
            Initialize();
        }

        if (_inputStream == null || _disposed)
        {
            _consecutiveNullFrames++;
            return null;
        }

            // Check if FFmpeg process has crashed
        if (_ffmpegProcess == null || _ffmpegProcess.HasExited)
        {
            Console.WriteLine("[NVENC] FFmpeg process has exited unexpectedly!");
            _consecutiveNullFrames = MAX_CONSECUTIVE_NULL_FRAMES; // Mark as unhealthy
            return null;
        }

        // LATENCY REDUCTION: If encoder is backing up (>1 frame), drop this input frame
        // and just return what's already in the queue to catch up.
        // Keeping 1 frame in queue allows for smoothness (jitter buffer), but >1 means we're lagging.
        // WARMUP: Disable dropping for first 30 frames to ensure stream establishment (IDR, etc.)
        bool isWarmup = _framesProcessed < 30;
        
        if (!isWarmup && _encodedFrames.Count > 1)
        {
            // Try to return the oldest frame to keep the stream moving
            if (_encodedFrames.TryDequeue(out var oldestFrame))
            {
                Console.WriteLine($"[NVENC] Dropping input to reduce latency (queue size: {_encodedFrames.Count + 1})");
                
                // If it's a real frame (not just headers), return it
                if (oldestFrame.Length >= 32) 
                {
                    _consecutiveNullFrames = 0;
                    return oldestFrame;
                }
            }
            // If we dropped input but didn't get a valid frame, return EMPTY to skip this frame
            // Returning null would trigger JPEG fallback which is slow
            return Array.Empty<byte>();
        }

        lock (_lock)
        {
            _framesProcessed++;
            try
            {
                // Write raw frame to FFmpeg stdin with timeout to prevent hanging
                // If FFmpeg's buffers are full (slow encoding), we skip this frame
                // WARMUP: Allow longer timeout (500ms) for first few frames to let FFmpeg spin up
                int writeTimeoutMs = isWarmup ? 500 : 50;
                
                var writeTask = Task.Run(() =>
                {
                    try
                    {
                        // PERFORMANCE OPTIMIZATION: Write BGRA directly to FFmpeg
                        // NVENC handles BGRA→YUV420 conversion in GPU hardware (zero CPU cost)
                        // Previous: CPU pixel-by-pixel conversion took 5-15ms @ 1080p
                        
                        // Convert from stride-aligned to packed format if needed
                        // Desktop Duplication uses BGRA (4 bytes/pixel), GDI uses BGR (3 bytes/pixel)
                        int bytesPerPixel = stride / width; // Auto-detect: 3 for BGR, 4 for BGRA
                        
                        if (bytesPerPixel == 4)
                        {
                            // 32-bit BGRA from Desktop Duplication - perfect for FFmpeg BGRA input!
                            if (stride == width * 4)
                            {
                                // Already packed, write directly (zero-copy best case)
                                // CRITICAL FIX: Write only the actual data size, not the full buffer capacity!
                                // The buffer might be larger than needed if we switched from a higher resolution.
                                int actualDataSize = width * height * 4;
                                _inputStream!.Write(rawFrame, 0, actualDataSize);
                            }
                            else
                            {
                                // Stride padding - pack rows (still much faster than pixel conversion)
                                var rowSize = width * 4;
                                for (int y = 0; y < height; y++)
                                {
                                    var srcOffset = y * stride;
                                    _inputStream!.Write(rawFrame, srcOffset, rowSize);
                                }
                            }
                        }
                        else if (bytesPerPixel == 3)
                        {
                            // 24-bit BGR from GDI - need to expand to BGRA for FFmpeg
                            // Add alpha channel (0xFF for opaque)
                            var rowSize = width * 4; // BGRA output
                            var bgraRow = new byte[rowSize];
                            
                            for (int y = 0; y < height; y++)
                            {
                                var srcOffset = y * stride;
                                
                                // Convert BGR to BGRA by adding alpha channel
                                for (int x = 0; x < width; x++)
                                {
                                    var srcPixel = srcOffset + (x * 3); // BGR = 3 bytes per pixel
                                    var dstPixel = x * 4; // BGRA = 4 bytes per pixel
                                    
                                    bgraRow[dstPixel + 0] = rawFrame[srcPixel + 0]; // B
                                    bgraRow[dstPixel + 1] = rawFrame[srcPixel + 1]; // G
                                    bgraRow[dstPixel + 2] = rawFrame[srcPixel + 2]; // R
                                    bgraRow[dstPixel + 3] = 0xFF; // A (opaque)
                                }
                                
                                _inputStream!.Write(bgraRow, 0, rowSize);
                            }
                        }
                        else
                        {
                            // Unexpected format - log warning and try direct write
                            Console.WriteLine($"[NVENC] WARNING: Unexpected bytes per pixel: {bytesPerPixel}, attempting direct write");
                            _inputStream!.Write(rawFrame, 0, Math.Min(rawFrame.Length, width * height * 4));
                        }
                        
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                });

                // Wait for write to complete (resilience against jitter)
                if (!writeTask.Wait(writeTimeoutMs))
                {
                    Console.WriteLine($"[NVENC] Write timeout ({writeTimeoutMs}ms) - FFmpeg too slow, skipping frame");
                    _consecutiveNullFrames++;
                    return Array.Empty<byte>(); // Skip frame instead of fallback
                }

                if (!writeTask.Result)
                {
                    Console.WriteLine("[NVENC] Write failed");
                    _consecutiveNullFrames++;
                    return null; // Fatal error, allow fallback/reset
                }

                // Wait for encoding with high-precision timing
                // Combine small NAL units (SPS, PPS, SEI) with the next video frame
                var waitStart = System.Diagnostics.Stopwatch.StartNew();
                const int SPIN_WAIT_MS = 2;    // Spin-wait for first 2ms (high precision)
                // WARMUP: Allow longer read wait (100ms) for first few frames
                int maxWaitMs = isWarmup ? 100 : 30; // Max wait 30ms total (~33 FPS min) normally
                const int MIN_FRAME_SIZE = 32; // Minimum size for actual video frames (static P-frames can be tiny)
                
                // Reuse a thread-local or pooled buffer would be ideal, but for now just prevent resizing
                // Allocating 256KB initial capacity covers most frames without resizing
                var combinedFrame = new List<byte>(256 * 1024); 
                
                while (waitStart.ElapsedMilliseconds < maxWaitMs)
                {
                    if (_encodedFrames.TryDequeue(out var encodedFrame))
                    {
                        combinedFrame.AddRange(encodedFrame);
                        
                        // If this NAL unit is large enough, it's an actual video frame
                        // Return the combined frame (headers + video data)
                        if (encodedFrame.Length >= MIN_FRAME_SIZE)
                        {
                            _consecutiveNullFrames = 0;
                            return combinedFrame.ToArray();
                        }
                        // Small NAL unit (SPS, PPS, SEI) - keep it and continue to get video frame
                        continue;
                    }
                    
                    if (waitStart.ElapsedMilliseconds < SPIN_WAIT_MS)
                    {
                        Thread.SpinWait(100);
                    }
                    else
                    {
                        Thread.Yield();
                    }
                }

                // Timeout waiting for frame - return EMPTY to skip frame
                // DO NOT return null, as that triggers slow JPEG fallback
                _consecutiveNullFrames++;
                return Array.Empty<byte>();
            }
            catch (IOException ioEx)
            {
                // Pipe broken - FFmpeg has likely crashed
                Console.WriteLine($"[NVENC] Pipe error (FFmpeg crashed?): {ioEx.Message}");
                _consecutiveNullFrames = MAX_CONSECUTIVE_NULL_FRAMES; // Mark as unhealthy
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NVENC] Error encoding frame: {ex.Message}");
                _consecutiveNullFrames++;
                return null;
            }
        }
    }

    private void ReadEncodedFrames()
    {
        if (_outputStream == null) return;

        // PERFORMANCE: Use buffer pooling to eliminate GC pressure from allocations
        var buffer = _bufferPool.Rent(1024 * 1024); // Rent 1MB buffer from pool
        var frameBuffer = new List<byte>();
        var nalStartPattern = new byte[] { 0x00, 0x00, 0x00, 0x01 }; // NAL unit start code
        int totalBytesRead = 0;
        int totalFrames = 0;

        try
        {
            Console.WriteLine("[NVENC] Read thread started, waiting for FFmpeg output...");
            while (!_disposed && _ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                var bytesRead = _outputStream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                {
                    // Yield instead of Sleep for faster response
                    Thread.Yield();
                    continue;
                }

                totalBytesRead += bytesRead;
                if (totalBytesRead < 10000 || totalBytesRead % 1000000 < bytesRead)
                {
                    Console.WriteLine($"[NVENC] Read {bytesRead} bytes from FFmpeg (total: {totalBytesRead / 1024} KB, frames queued: {totalFrames})");
                }

                frameBuffer.AddRange(buffer.Take(bytesRead));

                // Extract NAL units (H.264 frames)
                // Buffer small NAL units (SPS, PPS, SEI) and combine with video frames
                int searchStart = 0;
                while (true)
                {
                    int nalStart = FindPattern(frameBuffer, nalStartPattern, searchStart);
                    if (nalStart == -1) break;

                    // Find next NAL unit start
                    int nextNalStart = FindPattern(frameBuffer, nalStartPattern, nalStart + 4);
                    if (nextNalStart == -1)
                    {
                        // Last NAL unit, wait for more data
                        break;
                    }

                    // Extract NAL unit
                    var nalUnit = frameBuffer.Skip(nalStart).Take(nextNalStart - nalStart).ToArray();
                    
                    // Get NAL unit type (5 bits after start code)
                    int nalTypeOffset = nalStart + 4; // After 00 00 00 01
                    if (nalTypeOffset < frameBuffer.Count)
                    {
                        bool isHeader;
                        bool isVideoFrame;
                        int nalType;
                        if (_useHevc)
                        {
                            // HEVC: NAL type = (byte >> 1) & 0x3F
                            // 32=VPS, 33=SPS, 34=PPS, 35=AUD, 39/40=SEI; 0..31 = VCL (video) slices
                            nalType = (frameBuffer[nalTypeOffset] >> 1) & 0x3F;
                            isHeader = nalType == 32 || nalType == 33 || nalType == 34 || nalType == 35 || nalType == 39 || nalType == 40;
                            isVideoFrame = nalType <= 31;
                        }
                        else
                        {
                            // H.264: NAL type = byte & 0x1F
                            // 7=SPS, 8=PPS, 6=SEI, 9=AUD; 5=IDR, 1=non-IDR slice
                            nalType = frameBuffer[nalTypeOffset] & 0x1F;
                            isHeader = nalType == 7 || nalType == 8 || nalType == 6 || nalType == 9;
                            isVideoFrame = nalType == 5 || nalType == 1;
                        }
                        
                        if (isHeader)
                        {
                            // Buffer header NAL units, don't send separately
                            _pendingHeaders.AddRange(nalUnit);
                            if (totalFrames <= 10)
                            {
                                Console.WriteLine($"[NVENC] Buffering header NAL type {nalType}: {nalUnit.Length} bytes (total buffered: {_pendingHeaders.Count})");
                            }
                        }
                        else if (isVideoFrame)
                        {
                            // Combine any pending headers with this video frame
                            byte[] frameToSend;
                            if (_pendingHeaders.Count > 0)
                            {
                                frameToSend = new byte[_pendingHeaders.Count + nalUnit.Length];
                                _pendingHeaders.CopyTo(frameToSend, 0);
                                Array.Copy(nalUnit, 0, frameToSend, _pendingHeaders.Count, nalUnit.Length);
                                Console.WriteLine($"[NVENC] Combined {_pendingHeaders.Count} bytes of headers with {nalUnit.Length} byte frame = {frameToSend.Length} bytes");
                                _pendingHeaders.Clear();
                            }
                            else
                            {
                                frameToSend = nalUnit;
                            }
                            
                            _encodedFrames.Enqueue(frameToSend);
                            totalFrames++;
                            if (totalFrames <= 5 || totalFrames % 100 == 0)
                            {
                                Console.WriteLine($"[NVENC] Queued frame #{totalFrames}: {frameToSend.Length} bytes (queue size: {_encodedFrames.Count})");
                            }
                        }
                        else
                        {
                            // Unknown NAL type, send as-is
                            _encodedFrames.Enqueue(nalUnit);
                            totalFrames++;
                        }
                    }
                    else
                    {
                        // Can't determine NAL type, send as-is
                        _encodedFrames.Enqueue(nalUnit);
                        totalFrames++;
                    }

                    searchStart = nextNalStart;
                }

                // Remove processed data
                if (searchStart > 0)
                {
                    frameBuffer.RemoveRange(0, searchStart);
                }
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                Console.WriteLine($"[NVENC] Error reading encoded frames: {ex.Message}");
                Logging.CrashLogger.LogCrash("NVENC ReadEncodedFrames", ex);
            }
        }
        finally
        {
            // Return rented buffer to pool
            _bufferPool.Return(buffer);
        }
        
        Console.WriteLine($"[NVENC] Read thread exiting. Total bytes read: {totalBytesRead / 1024} KB, total frames: {totalFrames}");
        if (_ffmpegProcess != null && _ffmpegProcess.HasExited)
        {
            Console.WriteLine($"[NVENC] FFmpeg exited with code: {_ffmpegProcess.ExitCode}");
        }
    }

    private int FindPattern(List<byte> data, byte[] pattern, int startIndex)
    {
        for (int i = startIndex; i <= data.Count - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }

    public void SetBitrate(int bitrate)
    {
        // Note: Changing bitrate requires restarting FFmpeg
        // For now, just log it
        Console.WriteLine($"Bitrate change requested: {bitrate} (requires encoder restart)");
    }

    /// <summary>
    /// Check if the encoder is healthy and able to produce frames.
    /// Returns false if FFmpeg has crashed, pipe is broken, or too many consecutive failures.
    /// </summary>
    public bool IsHealthy()
    {
        if (_disposed || !_initialized) return false;
        if (_ffmpegProcess == null || _ffmpegProcess.HasExited) return false;
        if (_inputStream == null || _outputStream == null) return false;
        if (_consecutiveNullFrames >= MAX_CONSECUTIVE_NULL_FRAMES)
        {
            Console.WriteLine($"[NVENC] Encoder unhealthy: {_consecutiveNullFrames} consecutive null frames");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Force restart the encoder by disposing and reinitializing.
    /// </summary>
    public void ForceRestart()
    {
        Console.WriteLine("[NVENC] Force restarting encoder...");
        
        // Dispose existing resources
        _disposed = true;
        _inputStream?.Close();
        _outputStream?.Close();
        
        if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
        {
            try
            {
                _ffmpegProcess.Kill();
                _ffmpegProcess.WaitForExit(1000);
            }
            catch { }
            _ffmpegProcess.Dispose();
            _ffmpegProcess = null;
        }
        
        _readThread?.Join(1000);
        _readThread = null;
        _inputStream = null;
        _outputStream = null;
        
        // Clear queues
        // Clear any pending frames
        while (_encodedFrames.TryDequeue(out _)) { }
        
        // Reset state
        _initialized = false;
        _disposed = false;
        _consecutiveNullFrames = 0;
        
        // Reinitialize
        Initialize();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Console.WriteLine("[NVENC] Disposing encoder...");
        
        lock (_lock)
        {
            // Close streams first to signal FFmpeg to exit
            try { _inputStream?.Close(); } catch { }
            try { _outputStream?.Close(); } catch { }
            _inputStream = null;
            _outputStream = null;

            // Kill FFmpeg process
            if (_ffmpegProcess != null)
            {
                try
                {
                    if (!_ffmpegProcess.HasExited)
                    {
                        _ffmpegProcess.Kill();
                        _ffmpegProcess.WaitForExit(2000); // Wait longer for clean exit
                    }
                }
                catch { }
                
                try { _ffmpegProcess.Dispose(); } catch { }
                _ffmpegProcess = null;
            }

            // Wait for read thread to finish
            if (_readThread != null)
            {
                _readThread.Join(1000);
                _readThread = null;
            }
            
            // Clear any queued frames
            _encodedFrames.Clear();
        }

        _initialized = false;
        Console.WriteLine("[NVENC] Encoder disposed");
    }
}
