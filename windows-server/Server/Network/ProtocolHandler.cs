using System.IO;
using PCRemote.Shared.Protocol;

namespace PCRemote.Server.Network;

public class ProtocolHandler
{
    private readonly Stream _stream;
    private readonly byte[] _headerBuffer = new byte[6];
    private readonly SemaphoreSlim? _writeSemaphore;

    public ProtocolHandler(Stream stream, SemaphoreSlim? writeSemaphore = null)
    {
        _stream = stream;
        _writeSemaphore = writeSemaphore;
    }

    // Maximum incoming message size (10 MB) - protects against corrupted packets
    private const int MAX_INCOMING_MESSAGE_SIZE = 10 * 1024 * 1024;
    
    public async Task<ProtocolMessage> ReadMessageAsync(CancellationToken cancellationToken)
    {
        // Read header: Type (1) + Length (4) + Flags (1)
        await _stream.ReadExactlyAsync(_headerBuffer, cancellationToken);

        var type = (MessageType)_headerBuffer[0];
        // Read length as big-endian (network byte order) - use uint to avoid sign issues
        var length = (int)(((uint)_headerBuffer[1] << 24) | ((uint)_headerBuffer[2] << 16) | 
                          ((uint)_headerBuffer[3] << 8) | (uint)_headerBuffer[4]);
        var flags = _headerBuffer[5];

        // Validate length to prevent corrupted packet attacks or overflow
        if (length < 0 || length > MAX_INCOMING_MESSAGE_SIZE)
        {
            throw new InvalidOperationException($"Invalid message length: {length} bytes (max: {MAX_INCOMING_MESSAGE_SIZE}). Possible corrupted packet.");
        }

        // Read payload
        var data = new byte[length];
        if (length > 0)
        {
            await _stream.ReadExactlyAsync(data, cancellationToken);
        }

        return new ProtocolMessage
        {
            Type = type,
            Data = data,
            Flags = flags
        };
    }

    public async Task WriteMessageAsync(ProtocolMessage message, CancellationToken cancellationToken)
    {
        // Use semaphore if provided to serialize writes
        if (_writeSemaphore != null)
        {
            await _writeSemaphore.WaitAsync(cancellationToken);
            try
            {
                await WriteMessageInternalAsync(message, cancellationToken);
            }
            finally
            {
                _writeSemaphore.Release();
            }
        }
        else
        {
            await WriteMessageInternalAsync(message, cancellationToken);
        }
    }
    
    private async Task WriteMessageInternalAsync(ProtocolMessage message, CancellationToken cancellationToken)
    {
        var length = message.Data?.Length ?? 0;
        
        // Validate message size (prevent sending overly large messages)
        const int MAX_MESSAGE_SIZE = 10 * 1024 * 1024; // 10 MB max
        if (length > MAX_MESSAGE_SIZE)
        {
            throw new InvalidOperationException($"Message too large: {length} bytes (max: {MAX_MESSAGE_SIZE})");
        }
        
        var header = new byte[6];
        header[0] = (byte)message.Type;
        // Write length as big-endian (network byte order)
        header[1] = (byte)(length >> 24);
        header[2] = (byte)(length >> 16);
        header[3] = (byte)(length >> 8);
        header[4] = (byte)length;
        header[5] = message.Flags;

        await _stream.WriteAsync(header, cancellationToken);
        if (message.Data != null && message.Data.Length > 0)
        {
            // Write data in larger chunks for better throughput (128KB instead of 64KB)
            const int CHUNK_SIZE = 128 * 1024; // 128 KB chunks for better performance
            int offset = 0;
            while (offset < message.Data.Length)
            {
                int chunkSize = Math.Min(CHUNK_SIZE, message.Data.Length - offset);
                await _stream.WriteAsync(new ReadOnlyMemory<byte>(message.Data, offset, chunkSize), cancellationToken);
                offset += chunkSize;
            }
        }
        // Only flush every few frames to reduce overhead - the stream buffer will handle batching
        // Flush is still important but less frequent is better for throughput
        await _stream.FlushAsync(cancellationToken);
    }
}
