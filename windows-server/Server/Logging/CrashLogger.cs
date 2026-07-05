using System;
using System.IO;

namespace PCRemote.Server.Logging;

/// <summary>
/// Simple crash logger that writes exceptions to a file for post-mortem analysis
/// </summary>
public static class CrashLogger
{
    private static readonly string LogFilePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, 
        "crash_log.txt"
    );
    private static readonly object _lock = new();

    public static void LogCrash(string context, Exception ex)
    {
        lock (_lock)
        {
            try
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var logEntry = $"[{timestamp}] CRASH in {context}:\n" +
                              $"Exception: {ex.GetType().Name}\n" +
                              $"Message: {ex.Message}\n" +
                              $"Stack Trace:\n{ex.StackTrace}\n";
                
                if (ex.InnerException != null)
                {
                    logEntry += $"\nInner Exception: {ex.InnerException.GetType().Name}\n" +
                               $"Inner Message: {ex.InnerException.Message}\n" +
                               $"Inner Stack Trace:\n{ex.InnerException.StackTrace}\n";
                }
                
                logEntry += new string('-', 80) + "\n\n";
                
                File.AppendAllText(LogFilePath, logEntry);
                Console.WriteLine($"[CrashLogger] Logged crash to {LogFilePath}");
            }
            catch (Exception logEx)
            {
                Console.WriteLine($"[CrashLogger] Failed to write crash log: {logEx.Message}");
            }
        }
    }

    public static void LogMessage(string context, string message)
    {
        lock (_lock)
        {
            try
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var logEntry = $"[{timestamp}] {context}: {message}\n";
                File.AppendAllText(LogFilePath, logEntry);
            }
            catch
            {
                // Ignore logging errors
            }
        }
    }
}
