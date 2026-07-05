using System.Runtime.InteropServices;
using System.Linq;

namespace PCRemote.Server.ScreenCapture;

public class H264Encoder : IDisposable
{
    private IntPtr _encoderHandle = IntPtr.Zero;
    private bool _initialized = false;
    private readonly int _width;
    private readonly int _height;
    private readonly int _bitrate;
    private readonly int _fps;
    private readonly bool _useLossless;
    private readonly long _jpegQuality;

    public H264Encoder(int width, int height, int bitrate = 5000000, int fps = 60, bool useLossless = false, long jpegQuality = 95)
    {
        _width = width;
        _height = height;
        _bitrate = bitrate;
        _fps = fps;
        _useLossless = useLossless;
        _jpegQuality = jpegQuality;
    }

    public void Initialize()
    {
        // Initialize hardware encoder (NVENC/QuickSync)
        // This is a placeholder - would use actual hardware encoder APIs
        // For NVENC: Use NVIDIA Video Codec SDK
        // For QuickSync: Use Intel Media SDK
        _initialized = true;
    }

    public byte[]? EncodeFrame(byte[] rawFrame, int width, int height, int stride)
    {
        if (!_initialized)
        {
            Initialize();
        }

        // Placeholder for hardware encoding
        // In production, this would:
        // 1. Upload frame to GPU
        // 2. Use NVENC/QuickSync to encode to H.264
        // 3. Return encoded NAL units

        // For now, return a simplified representation
        // Real implementation would use:
        // - NVENC: nvenc.dll and NVIDIA Video Codec SDK
        // - QuickSync: libmfx.dll and Intel Media SDK
        
        return CompressFrame(rawFrame, width, height, stride);
    }

    private byte[] CompressFrame(byte[] rawFrame, int width, int height, int stride)
    {
        // Use JPEG compression as a temporary solution until hardware encoder is implemented
        // This provides good compression while maintaining reasonable quality
        
        if (rawFrame == null || rawFrame.Length == 0)
        {
            Console.WriteLine($"ERROR: CompressFrame received null or empty input (width: {width}, height: {height}, stride: {stride})");
            return Array.Empty<byte>();
        }
        
        if (width <= 0 || height <= 0)
        {
            Console.WriteLine($"ERROR: Invalid dimensions in CompressFrame: {width}x{height}");
            return Array.Empty<byte>();
        }
        
        // Pin the array to get a stable pointer
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(rawFrame, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using (var bitmap = new System.Drawing.Bitmap(width, height, stride, 
                System.Drawing.Imaging.PixelFormat.Format24bppRgb, 
                handle.AddrOfPinnedObject()))
            {
                // Pre-allocate MemoryStream with estimated capacity for faster writes
                // PNG for 1920x1080 is typically 1-3MB, so start with larger capacity
                // Use more conservative estimate to reduce memory allocations
                var estimatedSize = _useLossless ? 
                    (width * height * 2) : // PNG estimate: ~2x compressed (conservative)
                    (width * height * 3) / 10; // JPEG is ~10% of raw size
                using (var ms = new MemoryStream(estimatedSize))
                {
                    if (_useLossless)
                    {
                        // Use PNG (lossless) for perfect quality
                        // Note: System.Drawing PNG encoder doesn't support compression level control
                        // For maximum speed, we rely on the encoder's default fast compression
                        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    else
                    {
                        // Use very high quality JPEG for normal operation
                        var jpegCodec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                            .FirstOrDefault(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                        
                        if (jpegCodec != null)
                        {
                            // Use configured JPEG quality (99% for high quality, 95% default)
                            var encoderParams = new System.Drawing.Imaging.EncoderParameters(1);
                            encoderParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                                System.Drawing.Imaging.Encoder.Quality, _jpegQuality);
                            
                            bitmap.Save(ms, jpegCodec, encoderParams);
                            encoderParams.Dispose();
                        }
                        else
                        {
                            // Fallback if codec not found
                            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                        }
                    }
                    
                    // Optimize buffer copy - use GetBuffer() when possible to avoid extra allocation
                    if (ms.Length <= ms.Capacity)
                    {
                        var buffer = ms.GetBuffer();
                        // Direct return of buffer slice would be ideal, but we need a copy for safety
                        // Use ArrayPool or direct buffer access if possible in future
                        var result = new byte[ms.Length];
                        System.Buffer.BlockCopy(buffer, 0, result, 0, (int)ms.Length);
                        return result;
                    }
                    else
                    {
                        // Capacity exceeded - ToArray() is already optimized
                        return ms.ToArray();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR in CompressFrame: {ex.Message}");
            Console.WriteLine($"  Input: {rawFrame?.Length ?? 0} bytes, {width}x{height}, stride: {stride}");
            return Array.Empty<byte>();
        }
        finally
        {
            handle.Free();
        }
    }

    public void SetBitrate(int bitrate)
    {
        // Update encoder bitrate
        // Would reconfigure hardware encoder
    }

    public void Dispose()
    {
        if (_encoderHandle != IntPtr.Zero)
        {
            // Cleanup encoder resources
            _encoderHandle = IntPtr.Zero;
        }
        _initialized = false;
    }
}
