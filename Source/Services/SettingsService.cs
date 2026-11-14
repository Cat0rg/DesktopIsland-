using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using DesktopIsland.Models;
using Microsoft.Win32;

namespace DesktopIsland.Services;

public static class SettingsService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRunName = "DesktopIsland";
    private const string AutostartTaskName = "DesktopIsland_AutoStart";
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string SettingsFilePath => Path.Combine(LoggerService.ConfigDir, "settings.json");

    public static AppSettings Load() => Load(out _);

    public static AppSettings Load(out bool isFirstRun)
    {
        isFirstRun = false;
        try
        {
            Directory.CreateDirectory(LoggerService.ConfigDir);
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    loaded.PinnedApps ??= CreateDefaultApps();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            LoggerService.Error("Failed to load settings.json, falling back to defaults.", ex);
        }

        isFirstRun = true;
        var defaults = new AppSettings { PinnedApps = CreateDefaultApps() };
        Save(defaults);
        return defaults;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(LoggerService.ConfigDir);
            var tmp = SettingsFilePath + ".tmp";
            var json = JsonSerializer.Serialize(settings, JsonOpts);
            File.WriteAllText(tmp, json);
            File.Move(tmp, SettingsFilePath, overwrite: true);
            ApplyAutostart(settings.StartWithWindows);
        }
        catch (Exception ex)
        {
            LoggerService.Error("Failed to save settings.json.", ex);
        }
    }

    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static void ApplyAutostart(bool enable)
    {
        bool isAdmin = IsRunningAsAdministrator();

        if (!enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key?.GetValue(AppRunName) != null)
                    key.DeleteValue(AppRunName, throwOnMissingValue: false);
            }
            catch { }

            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", $"/Delete /TN \"{AutostartTaskName}\" /F")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(3000);
            }
            catch { }
            return;
        }

        var exePath = Path.Combine(LoggerService.BaseRootDir, "App", "DesktopIsland.exe");
        if (!File.Exists(exePath))
            exePath = Environment.ProcessPath ?? exePath;
        var workDir = Path.GetDirectoryName(exePath) ?? LoggerService.BaseRootDir;

        if (isAdmin)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key?.GetValue(AppRunName) != null)
                    key.DeleteValue(AppRunName, throwOnMissingValue: false);
            }
            catch { }

            try
            {
                string escExe = exePath.Replace("'", "''");
                string escDir = workDir.Replace("'", "''");
                string psScript =
                    $"$userId = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name; " +
                    $"$action = New-ScheduledTaskAction -Execute '{escExe}' -WorkingDirectory '{escDir}'; " +
                    $"$trigger = New-ScheduledTaskTrigger -AtLogOn -User $userId; " +
                    $"$principal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Highest; " +
                    $"$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew; " +
                    $"Register-ScheduledTask -TaskName '{AutostartTaskName}' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null";

                var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript.Replace("\"", "\\\"")}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(5000);
                if (proc?.ExitCode == 0)
                    return;
            }
            catch (Exception ex)
            {
                LoggerService.Error("Failed to register elevated autostart task via PowerShell.", ex);
            }
        }

        // Fallback if non-admin or PowerShell cmdlets unavailable
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.SetValue(AppRunName, $"\"{exePath}\"");
        }
        catch (Exception ex)
        {
            LoggerService.Error("Failed to update HKCU autostart fallback.", ex);
        }
    }

    private static List<PinnedApp> CreateDefaultApps()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingApp = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var candidates = new List<PinnedApp>
        {
            new() { Name = "Проводник", ExecutablePath = "explorer.exe" },
            new() { Name = "Chrome", ExecutablePath = Path.Combine(progFiles, @"Google\Chrome\Application\chrome.exe") },
            new() { Name = "Edge", ExecutablePath = Path.Combine(progFilesX86, @"Microsoft\Edge\Application\msedge.exe") },
            new() { Name = "Telegram", ExecutablePath = Path.Combine(roamingApp, @"Telegram Desktop\Telegram.exe") },
            new() { Name = "Discord", ExecutablePath = Path.Combine(localApp, @"Discord\Update.exe"), Arguments = "--processStart Discord.exe" },
            new() { Name = "Steam", ExecutablePath = Path.Combine(progFilesX86, @"Steam\steam.exe") },
            new() { Name = "VS Code", ExecutablePath = Path.Combine(localApp, @"Programs\Microsoft VS Code\Code.exe") },
            new() { Name = "VS Code Insiders", ExecutablePath = Path.Combine(localApp, @"Programs\Microsoft VS Code Insiders\Code - Insiders.exe") },
            new() { Name = "Notion", ExecutablePath = Path.Combine(localApp, @"Programs\Notion\Notion.exe") },
            new() { Name = "Блокнот", ExecutablePath = "notepad.exe" },
            new() { Name = "Калькулятор", ExecutablePath = "calc.exe" }
        };

        var result = new List<PinnedApp>();
        foreach (var app in candidates)
        {
            bool isBuiltin = app.ExecutablePath.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
                          || app.ExecutablePath.Equals("notepad.exe", StringComparison.OrdinalIgnoreCase)
                          || app.ExecutablePath.Equals("calc.exe", StringComparison.OrdinalIgnoreCase);

            if (isBuiltin || File.Exists(app.ExecutablePath))
            {
                result.Add(app);
                if (result.Count >= 6) break;
            }
        }

        if (result.Count == 0)
            result.Add(new PinnedApp { Name = "Проводник", ExecutablePath = "explorer.exe" });

        return result;
    }
}
