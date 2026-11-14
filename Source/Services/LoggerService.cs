using System;
using System.IO;

namespace DesktopIsland.Services;

public static class LoggerService
{
    private static readonly object Sync = new();

    public static string BaseRootDir
    {
        get
        {
            var exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var parent = Directory.GetParent(exeDir)?.FullName;
            if (!string.IsNullOrEmpty(parent) &&
                string.Equals(Path.GetFileName(exeDir), "App", StringComparison.OrdinalIgnoreCase))
            {
                return parent;
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopIsland");
        }
    }

    public static string LogsDir => Path.Combine(BaseRootDir, "Logs");
    public static string ConfigDir => Path.Combine(BaseRootDir, "Config");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null)
    {
        var full = ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
        Write("ERROR", full);
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogsDir);
                var logFile = Path.Combine(LogsDir, $"desktop-island-{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Never crash application due to logger I/O failure
        }
    }
}
