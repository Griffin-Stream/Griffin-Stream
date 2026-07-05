using System.Runtime.InteropServices;
using PCRemote.Shared.Protocol;

namespace PCRemote.Server.Input;

public class InputHandler
{
    [DllImport("user32.dll")]
    private static extern void SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(out CURSORINFO pci);
    
    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    private const int CURSOR_SHOWING = 0x00000001;
    
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    
    // Virtual screen metrics for multi-monitor support
    private const int SM_XVIRTUALSCREEN = 76;  // Left edge of virtual screen
    private const int SM_YVIRTUALSCREEN = 77;  // Top edge of virtual screen
    private const int SM_CXVIRTUALSCREEN = 78; // Width of virtual screen (all monitors)
    private const int SM_CYVIRTUALSCREEN = 79; // Height of virtual screen (all monitors)
    
    // Cached virtual screen metrics to avoid P/Invoke on every mouse input
    private static int _cachedVirtualLeft;
    private static int _cachedVirtualTop;
    private static int _cachedVirtualWidth;
    private static int _cachedVirtualHeight;
    private static DateTime _lastMetricsRefresh = DateTime.MinValue;
    private static readonly object _metricsLock = new();
    
    // Track button state across messages to detect transitions (press/release)
    private static byte _lastButtonState = 0;
    
    private static void RefreshScreenMetricsIfNeeded()
    {
        if ((DateTime.UtcNow - _lastMetricsRefresh).TotalSeconds < 2) return;
        lock (_metricsLock)
        {
            if ((DateTime.UtcNow - _lastMetricsRefresh).TotalSeconds < 2) return;
            _cachedVirtualLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
            _cachedVirtualTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
            _cachedVirtualWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            _cachedVirtualHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (_cachedVirtualWidth <= 0 || _cachedVirtualHeight <= 0)
            {
                _cachedVirtualLeft = 0;
                _cachedVirtualTop = 0;
                _cachedVirtualWidth = GetSystemMetrics(SM_CXSCREEN);
                _cachedVirtualHeight = GetSystemMetrics(SM_CYSCREEN);
                if (_cachedVirtualWidth <= 0) _cachedVirtualWidth = 1920;
                if (_cachedVirtualHeight <= 0) _cachedVirtualHeight = 1080;
            }
            _lastMetricsRefresh = DateTime.UtcNow;
        }
    }

    // Helper methods to read big-endian (network byte order) values
    private static int ReadBigEndianInt32(byte[] data, int offset)
    {
        // Mask all bytes to prevent sign extension issues
        return ((data[offset] & 0xFF) << 24) | 
               ((data[offset + 1] & 0xFF) << 16) | 
               ((data[offset + 2] & 0xFF) << 8) | 
               (data[offset + 3] & 0xFF);
    }
    
    private static short ReadBigEndianInt16(byte[] data, int offset)
    {
        // Mask all bytes to prevent sign extension issues
        return (short)(((data[offset] & 0xFF) << 8) | (data[offset + 1] & 0xFF));
    }

    public void HandleMouseInput(byte[] data, int offsetX, int offsetY)
    {
        if (data == null || data.Length < 9) return;

        try
        {
            // Read as big-endian (network byte order) to match Android client
            var x = ReadBigEndianInt32(data, 0);
            var y = ReadBigEndianInt32(data, 4);
            var buttons = data[8];
            var wheelDelta = data.Length > 9 ? ReadBigEndianInt16(data, 9) : (short)0;
            
            // Sanity check - reject obviously corrupted values
            if (Math.Abs(x) > 10000 || Math.Abs(y) > 10000)
            {
                Console.WriteLine($"[InputHandler] Rejecting corrupted mouse input: x={x}, y={y} (too large)");
                return;
            }

        // Get cached VIRTUAL screen bounds
        RefreshScreenMetricsIfNeeded();
        var virtualLeft = _cachedVirtualLeft;
        var virtualTop = _cachedVirtualTop;
        var virtualWidth = _cachedVirtualWidth;
        var virtualHeight = _cachedVirtualHeight;
        
        var virtualRight = virtualLeft + virtualWidth;
        var virtualBottom = virtualTop + virtualHeight;
        
        // Check if this is relative movement using the protocol flag convention:
        // Bit 0x80 in buttons byte = ABSOLUTE positioning (touch interactive mode)
        // No 0x80 flag with small delta values = RELATIVE movement (joystick)
        bool isAbsoluteFlag = (buttons & 0x80) != 0;
        bool isRelativeMovement = !isAbsoluteFlag && 
                                  Math.Abs(x) <= 100 && 
                                  Math.Abs(y) <= 100;
        
        // Prepare list of inputs to send
        var inputs = new List<INPUT>();

        if (isRelativeMovement)
        {
            if (x != 0 || y != 0)
            {
                var input = new INPUT { type = INPUT_MOUSE };
                input.U.mi.dx = x;
                input.U.mi.dy = y;
                input.U.mi.dwFlags = MOUSEEVENTF_MOVE;
                inputs.Add(input);
                
                // Console.WriteLine($"[InputHandler] Relative movement: ({x}, {y})");
            }
        }
        else
        {
            // Absolute position - NORMALIZE coordinates
            // Client sends 0-based coordinates relative to the video frame (not virtual screen)
            // We need to add the monitor offset to get actual Windows coordinates
            x += offsetX;
            y += offsetY;
            
            // Clamp to VIRTUAL screen boundaries
            x = Math.Max(virtualLeft, Math.Min(virtualRight - 1, x));
            y = Math.Max(virtualTop, Math.Min(virtualBottom - 1, y));
            
            // For SendInput absolute movement, we need normalized coordinates (0-65535)
            // spanning the entire virtual desktop
            int absX = (int)((x - virtualLeft) * 65535 / (virtualWidth - 1));
            int absY = (int)((y - virtualTop) * 65535 / (virtualHeight - 1));
            
            var input = new INPUT { type = INPUT_MOUSE };
            input.U.mi.dx = absX;
            input.U.mi.dy = absY;
            input.U.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
            inputs.Add(input);
            
            // Console.WriteLine($"[InputHandler] Absolute movement: ({x}, {y}) -> Normalized: ({absX}, {absY})");
        }

        // Handle button state transitions (mask out the absolute positioning flag bit 0x80)
        // The client sends the CURRENT button state, not click events:
        //   0x01 = left held, 0x02 = right held, 0x04 = middle held, 0x00 = all released
        // We detect transitions by comparing against the previous state and only
        // send DOWN on newly pressed buttons and UP on newly released buttons.
        // This enables sustained holds and drags.
        var buttonState = (byte)(buttons & 0x7F);
        byte changed = (byte)(buttonState ^ _lastButtonState);
        
        if ((changed & 0x01) != 0) // Left button changed
        {
            if ((buttonState & 0x01) != 0)
                inputs.Add(new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } } });
            else
                inputs.Add(new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } } });
        }
        if ((changed & 0x02) != 0) // Right button changed
        {
            if ((buttonState & 0x02) != 0)
                inputs.Add(new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_RIGHTDOWN } } });
            else
                inputs.Add(new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_RIGHTUP } } });
        }
        if ((changed & 0x04) != 0) // Middle button changed
        {
            if ((buttonState & 0x04) != 0)
                inputs.Add(new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_MIDDLEDOWN } } });
            else
                inputs.Add(new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_MIDDLEUP } } });
        }
        _lastButtonState = buttonState;

        // Handle wheel
        if (wheelDelta != 0)
        {
            var clampedWheelDelta = (short)Math.Clamp((int)wheelDelta, -120, 120);
            var input = new INPUT { type = INPUT_MOUSE };
            // Use unchecked cast to preserve two's complement for negative values
            input.U.mi.mouseData = unchecked((uint)clampedWheelDelta);
            input.U.mi.dwFlags = MOUSEEVENTF_WHEEL;
            inputs.Add(input);
        }
        
        // Send all inputs in one batch
        if (inputs.Count > 0)
        {
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[InputHandler] Error handling mouse input: {ex.Message}");
            // Don't throw - just log and continue
        }
    }

    public void HandleKeyboardInput(byte[] data)
    {
        if (data.Length < 2) return;

        var keyCode = data[0];
        var isKeyDown = data[1] != 0;

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = keyCode,
                    dwFlags = isKeyDown ? 0 : KEYEVENTF_KEYUP
                }
            }
        };
        
        SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
    }

    public void HandleTextInput(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var inputs = new List<INPUT>();

        foreach (char c in text)
        {
            // Key down
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE
                    }
                }
            });

            // Key up
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
                    }
                }
            });
        }

        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
    }

    private static readonly GamepadHandler _gamepadHandler = new();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static DateTime _lastFullscreenCheck = DateTime.MinValue;
    private static bool _isFullscreen = false;

    private static void CheckFullscreenAndHideCursor()
    {
        // Don't check too often (expensive P/Invoke) - check every 1 second
        if ((DateTime.Now - _lastFullscreenCheck).TotalMilliseconds < 1000)
        {
            if (_isFullscreen) HideCursor();
            return;
        }

        _lastFullscreenCheck = DateTime.Now;
        
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return;

            GetWindowRect(foreground, out var rect);
            int screenWidth = GetSystemMetrics(SM_CXSCREEN);
            int screenHeight = GetSystemMetrics(SM_CYSCREEN);

            // Simple fullscreen check: does window match (or exceed) screen size?
            // This works for most borderless windowed and exclusive fullscreen games
            _isFullscreen = (rect.Right - rect.Left >= screenWidth) && (rect.Bottom - rect.Top >= screenHeight);

            if (_isFullscreen)
            {
                // Move cursor to bottom right corner to hide it
                 HideCursor();
            }
        }
        catch { }
    }

    private static void HideCursor()
    {
        // Move cursor to potentially off-screen or corner to hide it
        // We use virtual screen dimensions to ensure it goes to the far corner
        int vW = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vH = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (vW == 0) vW = 1920;
        if (vH == 0) vH = 1080;
        
        SetCursorPos(vW, vH);
    }

    public void HandleGamepadInput(byte[] data)
    {
        if (data.Length < 12) return;

        // Protocols are Big-Endian (Network Byte Order)
        // BitConverter is Little-Endian on Windows (Intel/AMD)
        // We must manually parse Big-Endian inputs
        
        var leftX = (short)((data[0] << 8) | data[1]);
        var leftY = (short)((data[2] << 8) | data[3]);
        var rightX = (short)((data[4] << 8) | data[5]);
        var rightY = (short)((data[6] << 8) | data[7]);
        var buttons = (ushort)((data[8] << 8) | data[9]);
        var leftTrigger = data[10];
        var rightTrigger = data[11];

        // Debug logging for Gamepad values (Throttled?)
        // Only log if there's significant input to avoid spam
        if (buttons != 0 || Math.Abs(leftX) > 4000 || Math.Abs(leftY) > 4000) 
        {
             Console.WriteLine($"[InputHandler] Gamepad Input: LX={leftX}, LY={leftY}, RX={rightX}, RY={rightY}, BTN={buttons:X4}, LT={leftTrigger}, RT={rightTrigger}");
        }

        var gamepad = new GamepadHandler.XINPUT_GAMEPAD
        {
            sThumbLX = leftX,
            sThumbLY = leftY,
            sThumbRX = rightX,
            sThumbRY = rightY,
            wButtons = buttons,
            bLeftTrigger = leftTrigger,
            bRightTrigger = rightTrigger
        };

        _gamepadHandler.SetGamepadState(0, gamepad);
        
        // Auto-hide cursor if gaming
        // Optimization: Only check if there's actual input happening (sticks moved or buttons pressed)
        // Use a slightly larger deadzone (8000) to avoid ghost detections
        bool hasInput = buttons != 0 || 
                        Math.Abs(leftX) > 8000 || Math.Abs(leftY) > 8000 || 
                        Math.Abs(rightX) > 8000 || Math.Abs(rightY) > 8000;
                        
        // Auto-hide cursor logic REMOVED.
        // We defer to the game's native cursor handling.
        // Forcing the cursor to hide breaks games that use the mouse cursor for gamepad menus (e.g. Destiny 2).
    }

    public static void SendKey(byte keyCode)
    {
        var inputs = new INPUT[2];
        
        // Key down
        inputs[0] = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = keyCode, dwFlags = 0 }
            }
        };
        
        // Key up
        inputs[1] = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = keyCode, dwFlags = KEYEVENTF_KEYUP }
            }
        };
        
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
    }
    
    public static (int x, int y, bool visible) GetCursorState()
    {
        CURSORINFO pci = new CURSORINFO();
        pci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
        
        if (GetCursorInfo(out pci))
        {
            // If CURSOR_SHOWING is set, it's definitely visible.
            // If hCursor is valid (not null), it's likely visible even if flags=0 (common in some apps/browsers with custom cursors).
            bool isVisible = (pci.flags & CURSOR_SHOWING) != 0 || pci.hCursor != IntPtr.Zero;
            return (pci.ptScreenPos.X, pci.ptScreenPos.Y, isVisible);
        }
        
        // Fallback: If GetCursorInfo fails (rare), try GetCursorPos
        // Better to assume visible than to hide it erroneously
        if (GetCursorPos(out POINT point))
        {
            return (point.X, point.Y, true);
        }

        return (0, 0, false);
    }

    public static (int x, int y) GetCursorPosition()
    {
        if (GetCursorPos(out POINT point))
        {
            return (point.X, point.Y);
        }
        return (0, 0);
    }

    public static (int left, int top) GetVirtualScreenOffset()
    {
        var virtualLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var virtualTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        return (virtualLeft, virtualTop);
    }

    public static string GetGamepadStatus()
    {
        return _gamepadHandler.GetStatus();
    }
}
