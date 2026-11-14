using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DesktopIsland.Models;

namespace DesktopIsland.Services;

public static class CleanupService
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    public static Task<List<CleanupCategory>> AnalyzeAsync()
    {
        return Task.Run(() =>
        {
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userTemp = Path.GetTempPath();
            var winTemp = @"C:\Windows\Temp";
            var explorerDir = Path.Combine(localApp, @"Microsoft\Windows\Explorer");
            var werArchive = @"C:\ProgramData\Microsoft\Windows\WER\ReportArchive";
            var werQueue = @"C:\ProgramData\Microsoft\Windows\WER\ReportQueue";
            var crashDumps = Path.Combine(localApp, "CrashDumps");
            var nvDxCache = Path.Combine(localApp, @"NVIDIA\DXCache");
            var nvGlCache = Path.Combine(localApp, @"NVIDIA\GLCache");

            var list = new List<CleanupCategory>
            {
                new()
                {
                    Id = "user_temp",
                    Name = "Временные файлы пользователя (%TEMP%)",
                    Description = "Безопасные временные файлы приложений и установщиков",
                    SizeBytes = MeasureDirectory(userTemp),
                    IsSelected = true,
                    RequiresConfirmation = false
                },
                new()
                {
                    Id = "win_temp",
                    Name = "Временные файлы Windows (C:\\Windows\\Temp)",
                    Description = "Системные временные файлы (занятые файлы пропускаются)",
                    SizeBytes = MeasureDirectory(winTemp),
                    IsSelected = true,
                    RequiresConfirmation = false
                },
                new()
                {
                    Id = "thumbs",
                    Name = "Кэш миниатюр Проводника",
                    Description = "Файлы thumbcache_*.db (пересоздаются автоматически)",
                    SizeBytes = MeasureMatchingFiles(explorerDir, "thumbcache_*.db"),
                    IsSelected = true,
                    RequiresConfirmation = false
                },
                new()
                {
                    Id = "wer",
                    Name = "Отчёты об ошибках и дампы сбоев",
                    Description = "WER отчёты и локальные CrashDumps",
                    SizeBytes = MeasureDirectory(werArchive) + MeasureDirectory(werQueue) + MeasureDirectory(crashDumps),
                    IsSelected = true,
                    RequiresConfirmation = false
                },
                new()
                {
                    Id = "shader_cache",
                    Name = "Кэш шейдеров NVIDIA (DXCache / GLCache)",
                    Description = "Необязательный кэш шейдеров игр (пересоздаётся по мере игры)",
                    SizeBytes = MeasureDirectory(nvDxCache) + MeasureDirectory(nvGlCache),
                    IsSelected = false,
                    RequiresConfirmation = false
                },
                new()
                {
                    Id = "recycle_bin",
                    Name = "Корзина Windows (требует подтверждения)",
                    Description = "Удалённые файлы в Корзине — требует подтверждения перед очисткой",
                    SizeBytes = MeasureRecycleBin(),
                    IsSelected = false,
                    RequiresConfirmation = true
                }
            };

            return list;
        });
    }

    public static Task<CleanupResult> CleanAsync(IEnumerable<CleanupCategory> selectedCategories)
    {
        return Task.Run(() =>
        {
            long freed = 0;
            int deleted = 0;
            int skipped = 0;
            var details = new List<string>();

            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var explorerDir = Path.Combine(localApp, @"Microsoft\Windows\Explorer");

            foreach (var cat in selectedCategories)
            {
                if (!cat.IsSelected) continue;

                long catFreed = 0;
                int catDel = 0;
                int catSkip = 0;

                switch (cat.Id)
                {
                    case "user_temp":
                        CleanDirectory(Path.GetTempPath(), ref catFreed, ref catDel, ref catSkip);
                        break;
                    case "win_temp":
                        CleanDirectory(@"C:\Windows\Temp", ref catFreed, ref catDel, ref catSkip);
                        break;
                    case "thumbs":
                        CleanMatchingFiles(explorerDir, "thumbcache_*.db", ref catFreed, ref catDel, ref catSkip);
                        break;
                    case "wer":
                        CleanDirectory(@"C:\ProgramData\Microsoft\Windows\WER\ReportArchive", ref catFreed, ref catDel, ref catSkip);
                        CleanDirectory(@"C:\ProgramData\Microsoft\Windows\WER\ReportQueue", ref catFreed, ref catDel, ref catSkip);
                        CleanDirectory(Path.Combine(localApp, "CrashDumps"), ref catFreed, ref catDel, ref catSkip);
                        break;
                    case "shader_cache":
                        CleanDirectory(Path.Combine(localApp, @"NVIDIA\DXCache"), ref catFreed, ref catDel, ref catSkip);
                        CleanDirectory(Path.Combine(localApp, @"NVIDIA\GLCache"), ref catFreed, ref catDel, ref catSkip);
                        break;
                    case "recycle_bin":
                        long before = MeasureRecycleBin();
                        if (before > 0)
                        {
                            int hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                            if (hr == 0)
                            {
                                catFreed += before;
                                catDel += 1;
                            }
                        }
                        break;
                }

                freed += catFreed;
                deleted += catDel;
                skipped += catSkip;
                details.Add($"{cat.Name}: освобождено {CleanupCategory.FormatBytes(catFreed)}");
            }

            LoggerService.Info($"Cleanup completed: freed {CleanupCategory.FormatBytes(freed)}, deleted={deleted}, skipped={skipped}");
            return new CleanupResult
            {
                FreedBytes = freed,
                DeletedFilesCount = deleted,
                SkippedCount = skipped,
                Summary = string.Join(Environment.NewLine, details)
            };
        });
    }

    private static long MeasureDirectory(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long sum = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                try { sum += new FileInfo(file).Length; } catch { }
            }
        }
        catch { }
        return sum;
    }

    private static long MeasureMatchingFiles(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) return 0;
        long sum = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly))
            {
                try { sum += new FileInfo(file).Length; } catch { }
            }
        }
        catch { }
        return sum;
    }

    private static long MeasureRecycleBin()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            if (SHQueryRecycleBin(null, ref info) == 0)
                return Math.Max(0, info.i64Size);
        }
        catch { }
        return 0;
    }

    private static void CleanDirectory(string dir, ref long freed, ref int deleted, ref int skipped)
    {
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            try
            {
                var fi = new FileInfo(file);
                long len = fi.Length;
                fi.IsReadOnly = false;
                fi.Delete();
                freed += len;
                deleted++;
            }
            catch
            {
                skipped++;
            }
        }

        var dirs = Directory.EnumerateDirectories(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                            .OrderByDescending(d => d.Length);
        foreach (var sub in dirs)
        {
            try
            {
                if (Directory.Exists(sub) && Directory.GetFileSystemEntries(sub).Length == 0)
                    Directory.Delete(sub, false);
            }
            catch { }
        }
    }

    private static void CleanMatchingFiles(string dir, string pattern, ref long freed, ref int deleted, ref int skipped)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly))
        {
            try
            {
                var fi = new FileInfo(file);
                long len = fi.Length;
                fi.Delete();
                freed += len;
                deleted++;
            }
            catch
            {
                skipped++;
            }
        }
    }
}
