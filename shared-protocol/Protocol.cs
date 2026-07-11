namespace PCRemote.Shared.Protocol;

public enum MessageType : byte
{
    // Authentication
    AuthRequest = 0x01,
    AuthSuccess = 0x02,
    AuthFailure = 0x03,
    AuthChallenge = 0x04,   // Server -> client: random nonce for challenge-response auth
    AuthResponse = 0x05,    // Client -> server: ECDSA signature over the nonce
    Unpair = 0x06,          // Client -> server (authenticated): revoke this device's enrolled key
    
    // Screen streaming
    ScreenFrame = 0x10,
    ScreenFrameRequest = 0x11,
    ScreenConfig = 0x12,
    CursorPosition = 0x13,  // Server sends cursor position updates
    
    // Audio streaming
    AudioFrame = 0x14,  // Server sends encoded audio frames
    AudioConfig = 0x15,  // Server sends audio format configuration
    AudioFrameRequest = 0x16,  // Client requests audio (optional)
    
    // Input
    MouseInput = 0x20,
    KeyboardInput = 0x21,
    GamepadInput = 0x22,
    
    // Control
    Heartbeat = 0x30,
    HeartbeatResponse = 0x31,
    Disconnect = 0x32,
    
    // Configuration
    QualityChange = 0x40,
    BitrateChange = 0x41,
    // EncodingPreference (0x42) removed - lossless encoding is now automatic when zoomed
    MonitorSelect = 0x43,    // Client selects which monitor(s) to capture
    MonitorInfo = 0x44,      // Server sends monitor information to client
    StreamState = 0x45,      // Server tells client if the video is active or idle (static). Data: 1 byte (0=active, 1=idle)
    ServerInfo = 0x46,       // Server -> client (after auth): server tier (Free/Pro), capabilities, server/protocol version
    
    // Wake-on-LAN
    WOLRequest = 0x50,  // Client requests server to send WOL packet
    
    // Text Input
    TextInput = 0x24,  // Client sends text input (unicode characters)
}

public class ProtocolMessage
{
    public MessageType Type { get; set; }
    public byte[]? Data { get; set; }
    public byte Flags { get; set; }
}

/// <summary>
/// Video stream configuration negotiated by the client at connect time
/// (sent via <see cref="MessageType.ScreenConfig"/>). Fixed 16-byte big-endian wire layout.
/// </summary>
public class ScreenConfigData
{
    public const int Size = 16;

    // Codec values
    public const byte CodecH264 = 0;
    public const byte CodecHevc = 1;

    // Resolution modes
    public const byte ResNative = 0;
    public const byte ResMatchDevice = 1;
    public const byte Res720 = 2;
    public const byte Res1080 = 3;
    public const byte Res1440 = 4;

    // Quality modes
    public const byte QualityPerformance = 0;
    public const byte QualityBalanced = 1;
    public const byte QualityHigh = 2;
    public const byte QualityMax = 3;

    // Capability bitmask (what the client can decode)
    public const byte CapHevc = 0x01;
    public const byte CapMain10 = 0x02;

    public byte Version { get; set; } = 1;
    public byte Codec { get; set; } = CodecH264;
    public byte BitDepth { get; set; } = 8;       // 8 or 10
    public byte FpsCap { get; set; } = 60;
    public uint BitrateKbps { get; set; } = 15000;
    public byte ResolutionMode { get; set; } = ResNative;
    public byte QualityMode { get; set; } = QualityBalanced;
    public byte Adaptive { get; set; } = 0;       // 0/1
    public byte Capabilities { get; set; } = 0;   // CapHevc | CapMain10
    public ushort TargetWidth { get; set; } = 0;  // for ResMatchDevice
    public ushort TargetHeight { get; set; } = 0;
}

/// <summary>
/// Sent by the server right after authentication (<see cref="MessageType.ServerInfo"/>) to tell the
/// client whether it is a Free or Pro server, plus server/protocol version for compatibility nudges.
/// Fixed 8-byte layout. Older servers never send this, in which case the client assumes Free tier and
/// falls back to legacy (client-side) behavior. Deserialization is tolerant so the layout can grow.
/// </summary>
public class ServerInfoData
{
    public const int Size = 8;

    public const byte TierFree = 0;
    public const byte TierPro = 1;

    public byte MessageVersion { get; set; } = 1;
    public byte Tier { get; set; } = TierFree;
    public byte ProCapabilities { get; set; } = 0;   // reserved for future per-feature flags
    public byte VersionMajor { get; set; } = 0;
    public byte VersionMinor { get; set; } = 0;
    public byte VersionPatch { get; set; } = 0;
    public byte ProtocolVersion { get; set; } = 1;   // bump when the app/server wire contract changes
    public byte Reserved { get; set; } = 0;
}

public static class ProtocolSerializer
{
    public static byte[] SerializeServerInfo(ServerInfoData info)
    {
        var data = new byte[ServerInfoData.Size];
        data[0] = info.MessageVersion;
        data[1] = info.Tier;
        data[2] = info.ProCapabilities;
        data[3] = info.VersionMajor;
        data[4] = info.VersionMinor;
        data[5] = info.VersionPatch;
        data[6] = info.ProtocolVersion;
        data[7] = info.Reserved;
        return data;
    }

    public static ServerInfoData DeserializeServerInfo(byte[] data)
    {
        // Tolerant: require at least version + tier; default anything beyond that.
        if (data.Length < 2) throw new ArgumentException("Invalid server info data");
        var info = new ServerInfoData
        {
            MessageVersion = data[0],
            Tier = data[1],
        };
        if (data.Length > 2) info.ProCapabilities = data[2];
        if (data.Length > 3) info.VersionMajor = data[3];
        if (data.Length > 4) info.VersionMinor = data[4];
        if (data.Length > 5) info.VersionPatch = data[5];
        if (data.Length > 6) info.ProtocolVersion = data[6];
        if (data.Length > 7) info.Reserved = data[7];
        return info;
    }

    public static byte[] SerializeMouseInput(int x, int y, byte buttons, short wheelDelta = 0)
    {
        var data = new byte[11];
        BitConverter.GetBytes(x).CopyTo(data, 0);
        BitConverter.GetBytes(y).CopyTo(data, 4);
        data[8] = buttons;
        BitConverter.GetBytes(wheelDelta).CopyTo(data, 9);
        return data;
    }
    
    public static byte[] SerializeMouseRelativeMovement(int deltaX, int deltaY, byte buttons, short wheelDelta = 0)
    {
        var data = new byte[11];
        BitConverter.GetBytes(deltaX).CopyTo(data, 0);
        BitConverter.GetBytes(deltaY).CopyTo(data, 4);
        data[8] = buttons;
        BitConverter.GetBytes(wheelDelta).CopyTo(data, 9);
        return data;
    }

    public static (int x, int y, byte buttons, short wheelDelta) DeserializeMouseInput(byte[] data)
    {
        if (data.Length < 9) throw new ArgumentException("Invalid mouse input data");
        var x = BitConverter.ToInt32(data, 0);
        var y = BitConverter.ToInt32(data, 4);
        var buttons = data[8];
        var wheelDelta = data.Length > 9 ? BitConverter.ToInt16(data, 9) : (short)0;
        return (x, y, buttons, wheelDelta);
    }

    public static byte[] SerializeKeyboardInput(byte keyCode, bool isKeyDown)
    {
        return new byte[] { keyCode, (byte)(isKeyDown ? 1 : 0) };
    }

    public static (byte keyCode, bool isKeyDown) DeserializeKeyboardInput(byte[] data)
    {
        if (data.Length < 2) throw new ArgumentException("Invalid keyboard input data");
        return (data[0], data[1] != 0);
    }

    public static byte[] SerializeGamepadInput(short leftX, short leftY, short rightX, short rightY, ushort buttons, byte leftTrigger, byte rightTrigger)
    {
        var data = new byte[14];
        BitConverter.GetBytes(leftX).CopyTo(data, 0);
        BitConverter.GetBytes(leftY).CopyTo(data, 2);
        BitConverter.GetBytes(rightX).CopyTo(data, 4);
        BitConverter.GetBytes(rightY).CopyTo(data, 6);
        BitConverter.GetBytes(buttons).CopyTo(data, 8);
        data[10] = leftTrigger;
        data[11] = rightTrigger;
        return data;
    }

    public static (short leftX, short leftY, short rightX, short rightY, ushort buttons, byte leftTrigger, byte rightTrigger) DeserializeGamepadInput(byte[] data)
    {
        if (data.Length < 12) throw new ArgumentException("Invalid gamepad input data");
        var leftX = BitConverter.ToInt16(data, 0);
        var leftY = BitConverter.ToInt16(data, 2);
        var rightX = BitConverter.ToInt16(data, 4);
        var rightY = BitConverter.ToInt16(data, 6);
        var buttons = BitConverter.ToUInt16(data, 8);
        var leftTrigger = data[10];
        var rightTrigger = data[11];
        return (leftX, leftY, rightX, rightY, buttons, leftTrigger, rightTrigger);
    }

    public static byte[] SerializeAuthRequest(byte authType, byte[] authData)
    {
        var data = new byte[1 + authData.Length];
        data[0] = authType;
        authData.CopyTo(data, 1);
        return data;
    }

    public static (byte authType, byte[] authData) DeserializeAuthRequest(byte[] data)
    {
        if (data.Length < 1) throw new ArgumentException("Invalid auth request data");
        var authType = data[0];
        var authData = new byte[data.Length - 1];
        Array.Copy(data, 1, authData, 0, authData.Length);
        return (authType, authData);
    }

    public static byte[] SerializeTextInput(string text)
    {
        return System.Text.Encoding.UTF8.GetBytes(text);
    }

    public static string DeserializeTextInput(byte[] data)
    {
        return System.Text.Encoding.UTF8.GetString(data);
    }

    private static void WriteBeUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static uint ReadBeUInt32(byte[] data, int offset)
    {
        return ((uint)data[offset] << 24) |
               ((uint)data[offset + 1] << 16) |
               ((uint)data[offset + 2] << 8) |
               data[offset + 3];
    }

    private static void WriteBeUInt16(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }

    private static ushort ReadBeUInt16(byte[] data, int offset)
    {
        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    public static byte[] SerializeScreenConfig(ScreenConfigData config)
    {
        var data = new byte[ScreenConfigData.Size];
        data[0] = config.Version;
        data[1] = config.Codec;
        data[2] = config.BitDepth;
        data[3] = config.FpsCap;
        WriteBeUInt32(data, 4, config.BitrateKbps);
        data[8] = config.ResolutionMode;
        data[9] = config.QualityMode;
        data[10] = config.Adaptive;
        data[11] = config.Capabilities;
        WriteBeUInt16(data, 12, config.TargetWidth);
        WriteBeUInt16(data, 14, config.TargetHeight);
        return data;
    }

    public static ScreenConfigData DeserializeScreenConfig(byte[] data)
    {
        if (data.Length < ScreenConfigData.Size)
            throw new ArgumentException("Invalid screen config data");
        return new ScreenConfigData
        {
            Version = data[0],
            Codec = data[1],
            BitDepth = data[2],
            FpsCap = data[3],
            BitrateKbps = ReadBeUInt32(data, 4),
            ResolutionMode = data[8],
            QualityMode = data[9],
            Adaptive = data[10],
            Capabilities = data[11],
            TargetWidth = ReadBeUInt16(data, 12),
            TargetHeight = ReadBeUInt16(data, 14),
        };
    }

    public static byte[] SerializeBitrateChange(uint bitrateKbps)
    {
        var data = new byte[4];
        WriteBeUInt32(data, 0, bitrateKbps);
        return data;
    }

    public static uint DeserializeBitrateChange(byte[] data)
    {
        if (data.Length < 4) throw new ArgumentException("Invalid bitrate change data");
        return ReadBeUInt32(data, 0);
    }
}
