using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopIsland.Services;

public static class IconHelper
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? GetAppIcon(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;

        return Cache.GetOrAdd(exePath, path =>
        {
            try
            {
                string resolved = ResolvePath(path);
                if (!File.Exists(resolved)) return null;

                using Icon? icon = Icon.ExtractAssociatedIcon(resolved);
                if (icon == null) return null;

                var src = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch
            {
                return null;
            }
        });
    }

    private static string ResolvePath(string path)
    {
        if (File.Exists(path)) return path;
        if (path.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), path);
            if (File.Exists(candidate)) return candidate;
        }
        return path;
    }
}
