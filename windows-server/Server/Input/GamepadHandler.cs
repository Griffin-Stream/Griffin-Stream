using System.Runtime.InteropServices;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace PCRemote.Server.Input;

public class GamepadHandler : IDisposable
{
    private ViGEmClient? _client;
    private IXbox360Controller? _controller;
    private bool _useFallback = false;
    private bool _initialized = false;
    private readonly object _lock = new();

    // Mapping constants for legacy fallback
    private const int XINPUT_GAMEPAD_A = 0x1000;
    private const int XINPUT_GAMEPAD_B = 0x2000;
    private const int XINPUT_GAMEPAD_X = 0x4000;
    private const int XINPUT_GAMEPAD_Y = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    public GamepadHandler()
    {
        Initialize();
    }

    private void Initialize()
    {
        lock (_lock)
        {
            try
            {
                if (_initialized) return;

                Console.WriteLine("[GamepadHandler] Initializing ViGEm client...");
                _client = new ViGEmClient();
                _controller = _client.CreateXbox360Controller();
                _controller.AutoSubmitReport = true; // Auto-send updates
                
                // Subscribe to feedback (rumble) if needed in future
                _controller.FeedbackReceived += (sender, args) => 
                {
                    // We can send this back to the client for haptic feedback
                    // Console.WriteLine($"Rumble: L={args.LargeMotor}, S={args.SmallMotor}");
                };

                _controller.Connect();
                Console.WriteLine("Professional Grade Gamepad (ViGEm) initialized successfully.");
                _initialized = true;
                _useFallback = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GamepadHandler] Failed to initialize ViGEm (Driver likely missing): {ex.Message}");
                Console.WriteLine("[GamepadHandler] Falling back to Legacy WASD Keyboard emulation.");
                _useFallback = true;
                _initialized = true; // Initialized in fallback mode
                DisposeViGEm();
            }
        }
    }

    public void SetGamepadState(int controllerIndex, XINPUT_GAMEPAD gamepad)
    {
        if (!_initialized) Initialize();

        try
        {
            if (_useFallback || _controller == null)
            {
                SimulateGamepadInput(controllerIndex, gamepad);
                return;
            }

            // Map inputs to ViGEm controller
            // Buttons
            _controller.SetButtonState(Xbox360Button.A, (gamepad.wButtons & 0x1000) != 0);
            _controller.SetButtonState(Xbox360Button.B, (gamepad.wButtons & 0x2000) != 0);
            _controller.SetButtonState(Xbox360Button.X, (gamepad.wButtons & 0x4000) != 0);
            _controller.SetButtonState(Xbox360Button.Y, (gamepad.wButtons & 0x8000) != 0);
            
            _controller.SetButtonState(Xbox360Button.Up, (gamepad.wButtons & 0x0001) != 0);
            _controller.SetButtonState(Xbox360Button.Down, (gamepad.wButtons & 0x0002) != 0);
            _controller.SetButtonState(Xbox360Button.Left, (gamepad.wButtons & 0x0004) != 0);
            _controller.SetButtonState(Xbox360Button.Right, (gamepad.wButtons & 0x0008) != 0);
            
            _controller.SetButtonState(Xbox360Button.Start, (gamepad.wButtons & 0x0010) != 0);
            _controller.SetButtonState(Xbox360Button.Back, (gamepad.wButtons & 0x0020) != 0);
            
            _controller.SetButtonState(Xbox360Button.LeftThumb, (gamepad.wButtons & 0x0040) != 0);
            _controller.SetButtonState(Xbox360Button.RightThumb, (gamepad.wButtons & 0x0080) != 0);
            
            _controller.SetButtonState(Xbox360Button.LeftShoulder, (gamepad.wButtons & 0x0100) != 0);
            _controller.SetButtonState(Xbox360Button.RightShoulder, (gamepad.wButtons & 0x0200) != 0);

            // Triggers (0-255)
            _controller.SetSliderValue(Xbox360Slider.LeftTrigger, gamepad.bLeftTrigger);
            _controller.SetSliderValue(Xbox360Slider.RightTrigger, gamepad.bRightTrigger);

            // Analog Sticks (-32768 to 32767)
            _controller.SetAxisValue(Xbox360Axis.LeftThumbX, gamepad.sThumbLX);
            _controller.SetAxisValue(Xbox360Axis.LeftThumbY, gamepad.sThumbLY);
            _controller.SetAxisValue(Xbox360Axis.RightThumbX, gamepad.sThumbRX);
            _controller.SetAxisValue(Xbox360Axis.RightThumbY, gamepad.sThumbRY);
            
            // Note: AutoSubmitReport = true, so we don't need to call SubmitReport()
        }
        catch (Exception ex)
        {
            // If connection drops, switch to fallback to keep game playable
            Console.WriteLine($"[GamepadHandler] Error updating ViGEm controller: {ex.Message}. Switching to fallback.");
            _useFallback = true;
            DisposeViGEm();
            SimulateGamepadInput(controllerIndex, gamepad);
        }
    }

    private void SimulateGamepadInput(int controllerIndex, XINPUT_GAMEPAD gamepad)
    {
        // Legacy Fallback: Left stick -> WASD
        if (gamepad.sThumbLX < -8000) InputHandler.SendKey(0x41); // A
        if (gamepad.sThumbLX > 8000) InputHandler.SendKey(0x44); // D
        if (gamepad.sThumbLY < -8000) InputHandler.SendKey(0x53); // S (Y is inverted in some contexts, but typical mapping: Up=+ve)
        // Wait, standard XInput Y up is positive. Keyboard W is up.
        // My previous code had: if (gamepad.sThumbLY < -8000) SendKey(0x57); // W 
        // This implies Down (negative) -> W? That seems wrong?
        // Usually Y Up is positive. Let's correct it: Y > 8000 -> W
        if (gamepad.sThumbLY > 8000) InputHandler.SendKey(0x57); // W
        if (gamepad.sThumbLY < -8000) InputHandler.SendKey(0x53); // S
        
        // Buttons
        if ((gamepad.wButtons & XINPUT_GAMEPAD_A) != 0) InputHandler.SendKey(0x20); // Space
        if ((gamepad.wButtons & XINPUT_GAMEPAD_B) != 0) InputHandler.SendKey(0x1B); // Escape
        if ((gamepad.wButtons & XINPUT_GAMEPAD_X) != 0) InputHandler.SendKey(0x58); // X
        if ((gamepad.wButtons & XINPUT_GAMEPAD_Y) != 0) InputHandler.SendKey(0x59); // Y
    }

    public string GetStatus()
    {
        if (!_initialized) return "Uninitialized";
        if (_useFallback) return "Fallback Mode (Legacy Keyboard/Mouse Emulation) - ViGEm Driver Missing/Failed";
        return "Professional Grade Mode (ViGEm Active) - Virtual Xbox 360 Controller Connected";
    }

    private void DisposeViGEm()
    {
        try
        {
            _controller?.Disconnect();
            _controller = null;
            _client?.Dispose();
            _client = null;
        }
        catch { }
    }

    public void Dispose()
    {
        DisposeViGEm();
        GC.SuppressFinalize(this);
    }
}
