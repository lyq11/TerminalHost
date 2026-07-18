using System.Collections.Concurrent;
using System.Text;

namespace TerminalHost.Infrastructure;

public static class AppLog
{
    private const int MaxRecentEntries = 500;
    private static readonly object FileLock = new();
    private static readonly ConcurrentQueue<string> RecentEntries = new();
    private static readonly string LogFolder = Path.Combine(AppSettings.FolderPath, "logs");
    private static readonly string LogPath = Path.Combine(LogFolder, $"terminalhost-{DateTime.Now:yyyyMMdd}.log");

    public static string CurrentLogPath => LogPath;

    public static void Info(string message) => Write("INFO", message);
    public static void Audit(string message) => Write("AUDIT", message);
    public static void Error(string message, Exception? exception = null)
        => Write("ERROR", exception is null ? message : $"{message} | {exception}");

    public static string GetRecentText()
        => string.Join(Environment.NewLine, RecentEntries.ToArray());

    private static void Write(string level, string message)
    {
        string safeMessage = message.Replace("\r", " ").Replace("\n", " ");
        string line = $"{DateTimeOffset.Now:O} [{level}] {safeMessage}";
        RecentEntries.Enqueue(line);
        while (RecentEntries.Count > MaxRecentEntries)
            RecentEntries.TryDequeue(out _);

        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(LogFolder);
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never bring down the terminal host.
        }
    }
}
