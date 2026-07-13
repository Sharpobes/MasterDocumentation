using MasterDocumentation.Utilities;
using System.IO;

namespace MasterDocumentation.Services;

public static class LogService
{
    private static readonly object Gate = new();
    public static void Info(string message) => Write("INFO", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);
    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            AppPaths.Ensure();
            lock (Gate)
                File.AppendAllText(Path.Combine(AppPaths.Logs, $"master-{DateTime.Now:yyyy-MM-dd}.log"),
                    $"{DateTime.Now:O} [{level}] {message}{(ex is null ? "" : Environment.NewLine + ex)}{Environment.NewLine}");
        }
        catch { }
    }
}
