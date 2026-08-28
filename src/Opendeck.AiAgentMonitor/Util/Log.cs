namespace Opendeck.AiAgentMonitor.Util;

/// <summary>Minimal logger. OpenDeck captures the plugin's stdout/stderr into
/// ~/.local/share/opendeck/logs/plugins/&lt;uuid&gt;.log, so plain stderr is enough.</summary>
public static class Log
{
    public static bool Verbose { get; set; } = Environment.GetEnvironmentVariable("AIAGENTMONITOR_DEBUG") == "1";

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");
    public static void Debug(string message) { if (Verbose) Write("DEBUG", message); }

    private static void Write(string level, string message)
    {
        try { Console.Error.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}"); } catch { /* ignore */ }
    }
}
