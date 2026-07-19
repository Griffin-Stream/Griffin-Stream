using System.Collections.Concurrent;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace PCRemote.Server.Logging;

/// <summary>
/// Fans everything written to <see cref="Console"/> out to three sinks:
///   1. A capped in-memory ring buffer (so the debug terminal can replay history when shown).
///   2. A rolling log file under %LOCALAPPDATA%\GriffinStream\logs (always on, for support).
///   3. The real console window - but only while one is attached.
///
/// Console writes are pumped on a dedicated background thread so a stuck/selected console
/// (scrollbar drag, Quick Edit selection) cannot block capture/encode/stream threads.
/// The app ships as a WinExe (no console by default). The GUI's "Show debug log" toggle calls
/// <see cref="ShowConsole"/> which allocates a console, wires stdin/stdout to it, and replays the
/// buffered history so nothing is lost. <see cref="HideConsole"/> frees it again. The server keeps
/// running regardless of whether the terminal is visible.
/// </summary>
public sealed class ConsoleTee : TextWriter
{
    private const int MaxBufferedLines = 4000;
    private const int MaxConsoleQueueChunks = 2000;

    private static ConsoleTee? _instance;
    private static readonly object _consoleGate = new();

    private readonly object _gate = new();
    private readonly LinkedList<string> _buffer = new();
    private readonly StringBuilder _lineAccumulator = new();
    private readonly StreamWriter? _fileWriter;
    private readonly ConcurrentQueue<string> _consoleQueue = new();
    private readonly AutoResetEvent _consoleSignal = new(false);
    private StreamWriter? _consoleWriter;
    private Thread? _consolePump;
    private volatile bool _consolePumpRunning;

    public override Encoding Encoding => Encoding.UTF8;

    /// <summary>True while a debug console window is attached and visible.</summary>
    public static bool IsConsoleVisible { get; private set; }

    private ConsoleTee(StreamWriter? fileWriter) => _fileWriter = fileWriter;

    /// <summary>
    /// Install the tee as Console.Out / Console.Error. Safe to call once at startup. Failures are
    /// swallowed (logging must never take the server down).
    /// </summary>
    public static void Install()
    {
        if (_instance != null) return;
        StreamWriter? fileWriter = null;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GriffinStream", "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"server-{DateTime.Now:yyyyMMdd}.log");
            fileWriter = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true
            };
        }
        catch { /* no log file (e.g. read-only location); in-memory + console still work */ }

        _instance = new ConsoleTee(fileWriter);
        try
        {
            Console.SetOut(_instance);
            Console.SetError(_instance);
        }
        catch { /* leave defaults if the runtime refuses */ }
    }

    public override void Write(char value)
    {
        try { _fileWriter?.Write(value); } catch { }
        Accumulate(value);
        EnqueueConsole(value.ToString());
    }

    public override void Write(string? value)
    {
        if (value == null) return;
        try { _fileWriter?.Write(value); } catch { }
        foreach (var c in value) Accumulate(c);
        EnqueueConsole(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        try { _fileWriter?.Write(buffer, index, count); } catch { }
        for (int i = 0; i < count; i++) Accumulate(buffer[index + i]);
        EnqueueConsole(new string(buffer, index, count));
    }

    public override void Flush()
    {
        // File flush is best-effort; console is flushed by the pump thread.
        try { _fileWriter?.Flush(); } catch { }
        _consoleSignal.Set();
    }

    private void Accumulate(char c)
    {
        lock (_gate)
        {
            if (c == '\n')
            {
                var line = _lineAccumulator.ToString().TrimEnd('\r');
                _lineAccumulator.Clear();
                _buffer.AddLast(line);
                while (_buffer.Count > MaxBufferedLines) _buffer.RemoveFirst();
            }
            else
            {
                _lineAccumulator.Append(c);
            }
        }
    }

    private string[] SnapshotBuffer()
    {
        lock (_gate) return _buffer.ToArray();
    }

    /// <summary>
    /// Queue text for the console sink without blocking the caller. Drops oldest chunks if the
    /// console is stuck (selection/scrollbar) so memory and stream threads stay healthy.
    /// </summary>
    private void EnqueueConsole(string value)
    {
        if (!IsConsoleVisible || value.Length == 0) return;
        while (_consoleQueue.Count >= MaxConsoleQueueChunks && _consoleQueue.TryDequeue(out _)) { }
        _consoleQueue.Enqueue(value);
        _consoleSignal.Set();
    }

    private void StartConsolePump()
    {
        if (_consolePumpRunning) return;
        _consolePumpRunning = true;
        _consolePump = new Thread(ConsolePumpLoop)
        {
            IsBackground = true,
            Name = "GriffinConsolePump",
            Priority = ThreadPriority.BelowNormal
        };
        _consolePump.Start();
    }

    private void StopConsolePump()
    {
        _consolePumpRunning = false;
        _consoleSignal.Set();
        try { _consolePump?.Join(500); } catch { }
        _consolePump = null;
        while (_consoleQueue.TryDequeue(out _)) { }
    }

    private void ConsolePumpLoop()
    {
        while (_consolePumpRunning)
        {
            _consoleSignal.WaitOne(250);
            var writer = _consoleWriter;
            if (writer == null) continue;

            while (_consoleQueue.TryDequeue(out var chunk))
            {
                try
                {
                    writer.Write(chunk);
                }
                catch
                {
                    // Console detached / broken handle — drop the rest for this cycle.
                    break;
                }
            }

            try { writer.Flush(); } catch { }
        }
    }

    // ── Console attach / detach ──────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleTitle(string title);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_INPUT_HANDLE = -10;
    private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    private const uint ENABLE_EXTENDED_FLAGS = 0x0080;

    /// <summary>
    /// Disable Quick Edit so click/drag in the console does not pause the process waiting on
    /// console I/O. Combined with the async pump, scrollbar/selection no longer freezes streaming.
    /// </summary>
    private static void DisableQuickEditMode()
    {
        try
        {
            var inHandle = GetStdHandle(STD_INPUT_HANDLE);
            if (inHandle == IntPtr.Zero || inHandle == new IntPtr(-1)) return;
            if (!GetConsoleMode(inHandle, out uint mode)) return;
            mode |= ENABLE_EXTENDED_FLAGS;
            mode &= ~ENABLE_QUICK_EDIT_MODE;
            SetConsoleMode(inHandle, mode);
        }
        catch { /* best effort */ }
    }

    /// <summary>Allocate + show a console window and replay the buffered log into it. Thread-safe.</summary>
    public static void ShowConsole()
    {
        lock (_consoleGate)
        {
            if (IsConsoleVisible || _instance == null) return;
            try
            {
                if (!AllocConsole()) return;
                try { SetConsoleTitle("Griffin Stream Server - Debug Log"); } catch { }
                DisableQuickEditMode();

                // Point stdout at the freshly-allocated console.
                var outHandle = GetStdHandle(STD_OUTPUT_HANDLE);
                var outStream = new FileStream(new SafeFileHandle(outHandle, ownsHandle: false), FileAccess.Write);
                var consoleWriter = new StreamWriter(outStream, new UTF8Encoding(false)) { AutoFlush = true };

                // Point stdin at the console so interactive commands (devices/remove) work.
                var inHandle = GetStdHandle(STD_INPUT_HANDLE);
                var inStream = new FileStream(new SafeFileHandle(inHandle, ownsHandle: false), FileAccess.Read);
                Console.SetIn(new StreamReader(inStream));

                _instance._consoleWriter = consoleWriter;
                IsConsoleVisible = true;
                _instance.StartConsolePump();

                // Replay history so the user sees the full log, not just what happens next.
                // Enqueue (don't write inline) so AllocConsole setup never blocks the UI thread
                // on a sticky console, and the pump owns all CONOUT writes.
                _instance.EnqueueConsole("=== Griffin Stream Server - debug log (history replayed) ===\r\n");
                foreach (var line in _instance.SnapshotBuffer())
                    _instance.EnqueueConsole(line + "\r\n");
                _instance.EnqueueConsole("=== live log continues below ===\r\n");
            }
            catch
            {
                try { _instance.StopConsolePump(); } catch { }
                try { FreeConsole(); } catch { }
                _instance._consoleWriter = null;
                IsConsoleVisible = false;
            }
        }
    }

    /// <summary>Detach + hide the console window. The server keeps running. Thread-safe.</summary>
    public static void HideConsole()
    {
        lock (_consoleGate)
        {
            if (!IsConsoleVisible || _instance == null) return;
            try
            {
                _instance.StopConsolePump();
                _instance._consoleWriter = null;
                FreeConsole();
            }
            catch { /* best effort */ }
            finally { IsConsoleVisible = false; }
        }
    }

    /// <summary>Show the console if hidden, hide it if shown. Returns the new visible state.</summary>
    public static bool ToggleConsole()
    {
        if (IsConsoleVisible) HideConsole();
        else ShowConsole();
        return IsConsoleVisible;
    }
}
