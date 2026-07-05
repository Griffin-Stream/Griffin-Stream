using System.Diagnostics;

namespace PCRemote.Server.ScreenCapture;

public class BitrateController
{
    private readonly int _minBitrate;
    private readonly int _maxBitrate;
    private int _currentBitrate;
    private readonly Queue<long> _frameTimestamps = new();
    private readonly Queue<int> _latencyMeasurements = new();
    private DateTime _lastAdjustment = DateTime.UtcNow;
    private const int AdjustmentIntervalSeconds = 5;

    public BitrateController(int minBitrate = 1000000, int maxBitrate = 10000000)
    {
        _minBitrate = minBitrate;
        _maxBitrate = maxBitrate;
        _currentBitrate = (minBitrate + maxBitrate) / 2;
    }

    public int GetCurrentBitrate()
    {
        return _currentBitrate;
    }

    public void RecordFrameSent()
    {
        _frameTimestamps.Enqueue(Stopwatch.GetTimestamp());
        if (_frameTimestamps.Count > 60) // Keep last 60 frames
        {
            _frameTimestamps.Dequeue();
        }
    }

    public void RecordLatency(int latencyMs)
    {
        _latencyMeasurements.Enqueue(latencyMs);
        if (_latencyMeasurements.Count > 30) // Keep last 30 measurements
        {
            _latencyMeasurements.Dequeue();
        }
    }

    public void AdjustBitrate()
    {
        if ((DateTime.UtcNow - _lastAdjustment).TotalSeconds < AdjustmentIntervalSeconds)
        {
            return;
        }

        _lastAdjustment = DateTime.UtcNow;

        if (_latencyMeasurements.Count < 10)
        {
            return; // Not enough data
        }

        var avgLatency = _latencyMeasurements.Average();
        var frameRate = CalculateFrameRate();

        // Adjust based on latency and frame rate
        if (avgLatency > 100) // High latency
        {
            // Reduce bitrate
            _currentBitrate = Math.Max(_minBitrate, (int)(_currentBitrate * 0.8));
        }
        else if (avgLatency < 30 && frameRate > 55) // Low latency, good frame rate
        {
            // Increase bitrate
            _currentBitrate = Math.Min(_maxBitrate, (int)(_currentBitrate * 1.2));
        }
        else if (frameRate < 30) // Low frame rate
        {
            // Reduce bitrate to improve frame rate
            _currentBitrate = Math.Max(_minBitrate, (int)(_currentBitrate * 0.9));
        }
    }

    private double CalculateFrameRate()
    {
        if (_frameTimestamps.Count < 2)
        {
            return 0;
        }

        var timestamps = _frameTimestamps.ToArray();
        var totalTime = (timestamps[timestamps.Length - 1] - timestamps[0]) / (double)Stopwatch.Frequency;
        return (timestamps.Length - 1) / totalTime;
    }

    public void SetTargetBitrate(int bitrate)
    {
        _currentBitrate = Math.Clamp(bitrate, _minBitrate, _maxBitrate);
    }
}
