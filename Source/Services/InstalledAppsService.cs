using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DesktopIsland.Services;

public sealed class InstalledAppEntry
{
    public string Name { get; set; } = string.Empty;
    public string LaunchTarget { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
}

public static class InstalledAppsService
{
    private static List<InstalledAppEntry>? _cache;
    private static DateTime _lastScanUtc = DateTime.MinValue;
    private static readonly object Sync = new();

    private static readonly InstalledAppEntry[] BuiltInEntries =
    {
        new() { Name = "Калькулятор", LaunchTarget = "calc.exe", IconPath = Path.Combine(Environment.SystemDirectory, "calc.exe") },
        new() { Name = "Командная строка", LaunchTarget = "cmd.exe", IconPath = Path.Combine(Environment.SystemDirectory, "cmd.exe") },
        new() { Name = "Конфигурация системы", LaunchTarget = "msconfig.exe", IconPath = Path.Combine(Environment.SystemDirectory, "msconfig.exe") },
        new() { Name = "Камера", LaunchTarget = "microsoft.windows.camera:", IconPath = Path.Combine(Environment.SystemDirectory, "imageres.dll") },
        new() { Name = "Диспетчер задач", LaunchTarget = "taskmgr.exe", IconPath = Path.Combine(Environment.SystemDirectory, "taskmgr.exe") },
        new() { Name = "Проводник", LaunchTarget = "explorer.exe", IconPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe") },
        new() { Name = "Блокнот", LaunchTarget = "notepad.exe", IconPath = Path.Combine(Environment.SystemDirectory, "notepad.exe") },
        new() { Name = "Панель управления", LaunchTarget = "control.exe", IconPath = Path.Combine(Environment.SystemDirectory, "control.exe") },
        new() { Name = "Параметры Windows", LaunchTarget = "ms-settings:", IconPath = Path.Combine(Environment.SystemDirectory, "SystemSettingsAdminFlows.exe") },
        new() { Name = "PowerShell", LaunchTarget = "powershell.exe", IconPath = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe") },
        new() { Name = "Службы компонентов", LaunchTarget = "dcomcnfg.exe", IconPath = Path.Combine(Environment.SystemDirectory, "mmc.exe") },
        new() { Name = "Экранная клавиатура", LaunchTarget = "osk.exe", IconPath = Path.Combine(Environment.SystemDirectory, "osk.exe") },
        new() { Name = "Управление компьютером", LaunchTarget = "compmgmt.msc", IconPath = Path.Combine(Environment.SystemDirectory, "mmc.exe") },
        new() { Name = "Paint", LaunchTarget = "mspaint.exe", IconPath = Path.Combine(Environment.SystemDirectory, "mspaint.exe") },
        new() { Name = "Монитор ресурсов", LaunchTarget = "resmon.exe", IconPath = Path.Combine(Environment.SystemDirectory, "resmon.exe") }
    };

    public static List<InstalledAppEntry> GetCatalog(bool forceRefresh = false)
    {
        lock (Sync)
        {
            if (!forceRefresh && _cache != null && (DateTime.UtcNow - _lastScanUtc).TotalMinutes < 10)
                return _cache;

            var map = new Dictionary<string, InstalledAppEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var b in BuiltInEntries)
                map[b.Name] = b;

            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            };

            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                    {
                        var name = Path.GetFileNameWithoutExtension(file).Trim();
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (name.Contains("Uninstall", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Удалить", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Деинсталл", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Help", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Readme", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!map.ContainsKey(name))
                        {
                            map[name] = new InstalledAppEntry
                            {
                                Name = name,
                                LaunchTarget = file,
                                IconPath = file
                            };
                        }
                    }
                }
                catch { }
            }

            _cache = map.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            _lastScanUtc = DateTime.UtcNow;
            return _cache;
        }
    }

    public static List<InstalledAppEntry> Search(string query, int limit = 9)
    {
        var all = GetCatalog();
        string q = query?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(q))
            return all.Take(limit).ToList();

        var prefix = all.Where(a => a.Name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase));
        var contains = all.Where(a => !a.Name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase) &&
                                      a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        return prefix.Concat(contains).Take(limit).ToList();
    }
}
