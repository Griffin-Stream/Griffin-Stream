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
/// The app ships as a WinExe (no console by default). The GUI's "Show debug log" toggle calls
/// <see cref="ShowConsole"/> which allocates a console, wires stdin/stdout to it, and replays the
/// buffered history so nothing is lost. <see cref="HideConsole"/> frees it again. The server keeps
/// running regardless of whether the terminal is visible.
/// </summary>
public sealed class ConsoleTee : TextWriter
{
    private const int MaxBufferedLines = 4000;

    private static ConsoleTee? _instance;
    private static readonly object _consoleGate = new();

    private readonly object _gate = new();
    private readonly LinkedList<string> _buffer = new();
    private readonly StringBuilder _lineAccumulator = new();
    private readonly StreamWriter? _fileWriter;
    private StreamWriter? _consoleWriter;

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
        _consoleWriter?.Write(value);
        _fileWriter?.Write(value);
        Accumulate(value);
    }

    public override void Write(string? value)
    {
        if (value == null) return;
        _consoleWriter?.Write(value);
        _fileWriter?.Write(value);
        foreach (var c in value) Accumulate(c);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        _consoleWriter?.Write(buffer, index, count);
        _fileWriter?.Write(buffer, index, count);
        for (int i = 0; i < count; i++) Accumulate(buffer[index + i]);
    }

    public override void Flush()
    {
        try { _consoleWriter?.Flush(); } catch { }
        try { _fileWriter?.Flush(); } catch { }
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

    // ── Console attach / detach ──────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleTitle(string title);

    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_INPUT_HANDLE = -10;

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

                // Replay history so the user sees the full log, not just what happens next.
                consoleWriter.WriteLine("=== Griffin Stream Server - debug log (history replayed) ===");
                foreach (var line in _instance.SnapshotBuffer()) consoleWriter.WriteLine(line);
                consoleWriter.WriteLine("=== live log continues below ===");
                consoleWriter.Flush();
            }
            catch
            {
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
