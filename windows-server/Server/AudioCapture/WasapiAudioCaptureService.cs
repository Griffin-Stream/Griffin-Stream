using NAudio.Wave;

namespace PCRemote.Server.AudioCapture;

/// <summary>
/// WASAPI-based audio capture service using NAudio for native Windows audio loopback
/// and Concentus for Opus encoding. No third-party drivers (VB-Cable) required.
/// </summary>
public class WasapiAudioCaptureService : IDisposable
{
    private readonly int _sampleRate;
    private readonly int _channels;
    private WasapiLoopbackCapture? _capture;
    private bool _initialized = false;
    private readonly object _lock = new();
    private readonly Queue<byte[]> _audioFrames = new();
    private bool _disposed = false;
    private readonly int _frameDurationMs;
    private byte[] _currentFrameBuffer = Array.Empty<byte>(); // Pre-allocated buffer for current frame
    private int _currentFrameOffset = 0;
    private int _bytesPerFrame; // Store calculated frame size

    public WasapiAudioCaptureService(int sampleRate = 48000, int channels = 2, int frameDurationMs = 10)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _frameDurationMs = frameDurationMs;
        
        // Calculate samples per frame (e.g., 48000 Hz * 0.010 sec * 2 channels = 960 samples)
        // Calculate samples per frame (e.g., 48000 Hz * 0.010 sec * 2 channels = 960 samples)
        int samplesPerFrame = _sampleRate * _frameDurationMs / 1000;
        _bytesPerFrame = samplesPerFrame * _channels * 2; // 2 bytes per int16 sample
        _currentFrameBuffer = new byte[_bytesPerFrame];
    }

    public void Initialize()
    {
        if (_initialized) return;

        lock (_lock)
        {
            try
            {
                Console.WriteLine("[WASAPI] Initializing native Windows audio capture (PCM Mode)...");
                
                // Create WASAPI loopback capture (captures system audio output)
                _capture = new WasapiLoopbackCapture();
                
                // Log capture format
                Console.WriteLine($"[WASAPI] Capture format: {_capture.WaveFormat.SampleRate} Hz, " +
                                  $"{_capture.WaveFormat.Channels} channels, " +
                                  $"{_capture.WaveFormat.BitsPerSample}-bit");
                
                // Verify the capture matches our target format
                if (_capture.WaveFormat.SampleRate != _sampleRate || _capture.WaveFormat.Channels != _channels)
                {
                    Console.WriteLine($"[WASAPI] WARNING: Capture format mismatch! " +
                                      $"Expected {_sampleRate}Hz/{_channels}ch, " +
                                      $"Got {_capture.WaveFormat.SampleRate}Hz/{_capture.WaveFormat.Channels}ch");
                }
                
                // PCM Mode: No encoder needed. We send raw 16-bit samples.
                Console.WriteLine($"[WASAPI] PCM Audio enabled: {_sampleRate} Hz, {_channels} channels, 16-bit, {_frameDurationMs}ms frames");
                
                // Set up data available event
                _capture.DataAvailable += OnDataAvailable;
                
                // Start capture
                _capture.StartRecording();
                
                _initialized = true;
                Console.WriteLine("[WASAPI] ✓ Audio capture started successfully!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WASAPI] ERROR: Initialization failed: {ex.Message}");
                _capture?.Dispose();
                _capture = null;
                throw;
            }
        }
    }


    // Removed unused GenerateOpusHeader method


    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_disposed) return;

        try
        {
            // Convert captured PCM data (float32) to int16
            // NAudio WASAPI captures as float32
            int bytesPerSample = _capture!.WaveFormat.BitsPerSample / 8;
            int samplesRecorded = e.BytesRecorded / bytesPerSample;
            
            // Temporary buffer for float samples
            var floatSamples = new float[samplesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, floatSamples, 0, e.BytesRecorded);
            
            // Convert float32 to int16
            for (int i = 0; i < samplesRecorded; i++)
            {
                // Clamp to [-1.0, 1.0] and convert to int16
                float sample = Math.Max(-1.0f, Math.Min(1.0f, floatSamples[i]));
                short sh = (short)(sample * short.MaxValue);
                
                // Fast write to fixed buffer
                _currentFrameBuffer[_currentFrameOffset++] = (byte)(sh & 0xFF);
                _currentFrameBuffer[_currentFrameOffset++] = (byte)((sh >> 8) & 0xFF);
                
                // If buffer is full, emit frame
                if (_currentFrameOffset >= _bytesPerFrame)
                {
                    // Create a copy to enqueue (since we're reusing the buffer)
                    var framePcm = new byte[_bytesPerFrame];
                    Array.Copy(_currentFrameBuffer, framePcm, _bytesPerFrame);
                    
                    _currentFrameOffset = 0; // Reset for next frame
                    
                    lock (_lock)
                    {
                        _audioFrames.Enqueue(framePcm);
                        
                        // Limit queue size (keep it tight for low latency)
                        while (_audioFrames.Count > 20) // 20 frames = 200ms buffer max
                        {
                            _audioFrames.Dequeue();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WASAPI] ERROR in DataAvailable: {ex.Message}");
        }
    }

    public void ClearFrameQueue()
    {
        lock (_lock)
        {
            _currentFrameOffset = 0; // Reset incomplete frame
            while (_audioFrames.TryDequeue(out _)) { }
        }
    }

    public byte[]? GetOpusHeader()
    {
        return null; // No header for PCM
    }

    public async Task<byte[]?> CaptureFrameAsync(CancellationToken cancellationToken)
    {
        if (!_initialized || _disposed)
        {
            return null;
        }

        // Try to get an audio frame from the queue
        lock (_lock)
        {
            if (_audioFrames.TryDequeue(out var audioFrame))
            {
                return audioFrame;
            }
        }

        // If no frame available, yield
        await Task.Delay(1, cancellationToken);
        return null;
    }

    /// <summary>
    /// Tear down and recreate WASAPI loopback so a stalled idle session does not survive reconnect.
    /// </summary>
    public void ForceRestart()
    {
        if (_disposed) return;

        Console.WriteLine("[WASAPI] Force restart requested...");
        StopCapture();
        Initialize();
    }

    /// <summary>
    /// Stop capture and clear buffers without marking the service disposed (safe to Initialize again).
    /// </summary>
    public void StopCapture()
    {
        lock (_lock)
        {
            if (_capture != null)
            {
                try { _capture.DataAvailable -= OnDataAvailable; } catch { }
                try { _capture.StopRecording(); } catch { }
                try { _capture.Dispose(); } catch { }
                _capture = null;
            }

            _currentFrameOffset = 0;
            while (_audioFrames.TryDequeue(out _)) { }
            _initialized = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopCapture();
    }
}
