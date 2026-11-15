using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopIsland.Services;

public static class IconHelper
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static ImageSource? GetAppIcon(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;

        return Cache.GetOrAdd(exePath, path =>
        {
            try
            {
                string resolved = ResolvePath(path);
                if (File.Exists(resolved))
                {
                    // Use SHGetFileInfo first (handles .lnk shortcuts and .exe seamlessly)
                    var shfi = new SHFILEINFO();
                    IntPtr hImg = SHGetFileInfo(resolved, 0, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
                    if (hImg != IntPtr.Zero && shfi.hIcon != IntPtr.Zero)
                    {
                        try
                        {
                            var src = Imaging.CreateBitmapSourceFromHIcon(shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            src.Freeze();
                            return src;
                        }
                        finally
                        {
                            DestroyIcon(shfi.hIcon);
                        }
                    }

                    using Icon? icon = Icon.ExtractAssociatedIcon(resolved);
                    if (icon != null)
                    {
                        var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        src.Freeze();
                        return src;
                    }
                }
            }
            catch { }
            return null;
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
