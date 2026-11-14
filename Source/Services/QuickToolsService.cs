using System;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using DesktopIsland.Models;

namespace DesktopIsland.Services;

public static class QuickToolsService
{
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const int SC_MONITORPOWER = 0xF170;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("Powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static (bool Success, bool IsNowPinned, string AppName) TogglePinExternalWindow(IntPtr targetHwnd)
    {
        if (targetHwnd == IntPtr.Zero || !IsWindow(targetHwnd))
            return (false, false, string.Empty);

        try
        {
            GetWindowThreadProcessId(targetHwnd, out uint pid);
            string appName = "Окно";
            try
            {
                if (pid > 0)
                {
                    using var p = Process.GetProcessById((int)pid);
                    appName = p.ProcessName;
                }
            }
            catch { }

            bool currentlyTopmost = (GetWindowLong(targetHwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
            bool nextPinned = !currentlyTopmost;
            SetWindowPos(targetHwnd, nextPinned ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
            return (true, nextPinned, appName);
        }
        catch
        {
            return (false, false, string.Empty);
        }
    }

    public static bool ToggleDesktopIcons()
    {
        try
        {
            IntPtr defView = IntPtr.Zero;
            var progman = FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
                defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);

            if (defView == IntPtr.Zero)
            {
                var worker = IntPtr.Zero;
                while ((worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
                {
                    defView = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (defView != IntPtr.Zero) break;
                }
            }

            if (defView != IntPtr.Zero)
            {
                SendMessage(defView, WM_COMMAND, new IntPtr(0x7402), IntPtr.Zero);
                return true;
            }
        }
        catch { }
        return false;
    }

    public static void TakeScreenshot()
    {
        try { Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true }); } catch { }
    }

    public static void LockPc()
    {
        try { LockWorkStation(); } catch { }
    }

    public static void SleepPc()
    {
        try { SetSuspendState(false, false, false); } catch { }
    }

    public static void TurnOffMonitors()
    {
        try { SendMessage(HWND_BROADCAST, WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(2)); } catch { }
    }

    private static string? _savedPowerSchemeGuid;

    public static bool EnterGameHighPerformancePlan()
    {
        try
        {
            var activeOut = RunPowerCfg("/GETACTIVESCHEME");
            var activeMatch = System.Text.RegularExpressions.Regex.Match(
                activeOut ?? string.Empty,
                @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            if (!activeMatch.Success) return false;

            string curGuid = activeMatch.Value;
            const string highPerfGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
            const string ultimatePerfGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

            if (curGuid.Equals(highPerfGuid, StringComparison.OrdinalIgnoreCase) ||
                curGuid.Equals(ultimatePerfGuid, StringComparison.OrdinalIgnoreCase))
                return false;

            var listOut = RunPowerCfg("/LIST") ?? string.Empty;
            string? targetGuid = null;
            if (listOut.Contains(ultimatePerfGuid, StringComparison.OrdinalIgnoreCase))
                targetGuid = ultimatePerfGuid;
            else if (listOut.Contains(highPerfGuid, StringComparison.OrdinalIgnoreCase))
                targetGuid = highPerfGuid;

            if (targetGuid == null) return false;

            _savedPowerSchemeGuid = curGuid;
            RunPowerCfg($"/SETACTIVE {targetGuid}");
            LoggerService.Info($"Game Boost: switched power plan {curGuid} -> {targetGuid}");
            return true;
        }
        catch { return false; }
    }

    public static void RestorePreviousPowerPlan()
    {
        if (string.IsNullOrEmpty(_savedPowerSchemeGuid)) return;
        try
        {
            var guid = _savedPowerSchemeGuid;
            _savedPowerSchemeGuid = null;
            RunPowerCfg($"/SETACTIVE {guid}");
            LoggerService.Info($"Game Boost: restored power plan -> {guid}");
        }
        catch { }
    }

    private static string? RunPowerCfg(string args)
    {
        var psi = new ProcessStartInfo("powercfg.exe", args)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return null;
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(1500);
        return output;
    }

    public static async Task<WeatherInfo?> FetchWeatherAsync(string? cityQuery)
    {
        try
        {
            double lat = 0, lon = 0;
            string cityName = cityQuery?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(cityName))
            {
                // Auto detect city via ipwho.is (HTTPS free, no key)
                var ipJson = await Http.GetStringAsync("https://ipwho.is/?fields=city,latitude,longitude,success");
                using var doc = JsonDocument.Parse(ipJson);
                if (doc.RootElement.TryGetProperty("success", out var ok) && ok.GetBoolean())
                {
                    cityName = doc.RootElement.GetProperty("city").GetString() ?? "Авто";
                    lat = doc.RootElement.GetProperty("latitude").GetDouble();
                    lon = doc.RootElement.GetProperty("longitude").GetDouble();
                }
                else return null;
            }
            else
            {
                var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(cityName)}&count=1&language=ru";
                var geoJson = await Http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(geoJson);
                if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                    return null;
                var first = results[0];
                cityName = first.GetProperty("name").GetString() ?? cityName;
                lat = first.GetProperty("latitude").GetDouble();
                lon = first.GetProperty("longitude").GetDouble();
            }

            var weatherUrl = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}&longitude={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}&current_weather=true";
            var wJson = await Http.GetStringAsync(weatherUrl);
            using var wDoc = JsonDocument.Parse(wJson);
            var cw = wDoc.RootElement.GetProperty("current_weather");
            double temp = cw.GetProperty("temperature").GetDouble();
            int code = cw.GetProperty("weathercode").GetInt32();

            return new WeatherInfo
            {
                City = cityName,
                TempC = temp,
                IconGlyph = MapWmoCode(code)
            };
        }
        catch
        {
            return null;
        }
    }

    private static string MapWmoCode(int code) => code switch
    {
        0 => "☀",
        1 or 2 => "⛅",
        3 => "☁",
        45 or 48 => "🌫",
        >= 51 and <= 67 => "🌧",
        >= 71 and <= 77 => "❄",
        >= 80 and <= 82 => "🌦",
        >= 95 => "⛈",
        _ => "🌡"
    };
}
