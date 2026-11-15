using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DesktopIsland.Models;
using Microsoft.Win32;

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

    public static bool IsDoNotDisturbEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings");
            if (key?.GetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED") is int v)
                return v == 0;
        }
        catch { }
        return false;
    }

    public static bool ToggleDoNotDisturb()
    {
        try
        {
            bool nextDnd = !IsDoNotDisturbEnabled();
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings");
            key.SetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED", nextDnd ? 0 : 1, RegistryValueKind.DWord);
            return nextDnd;
        }
        catch { return false; }
    }

    public static bool IsHighPerformancePlanActive()
    {
        try
        {
            var activeOut = RunPowerCfg("/GETACTIVESCHEME");
            if (string.IsNullOrEmpty(activeOut)) return false;
            return activeOut.Contains("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", StringComparison.OrdinalIgnoreCase)
                || activeOut.Contains("e9a42b02-d5df-448d-aa00-03f14749eb61", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool ToggleHighPerformancePlan()
    {
        const string balancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
        const string highPerfGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        const string ultimatePerfGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
        try
        {
            if (IsHighPerformancePlanActive())
            {
                RunPowerCfg($"/SETACTIVE {balancedGuid}");
                return false;
            }

            var listOut = RunPowerCfg("/LIST") ?? string.Empty;
            string? target = listOut.Contains(ultimatePerfGuid, StringComparison.OrdinalIgnoreCase)
                ? ultimatePerfGuid
                : (listOut.Contains(highPerfGuid, StringComparison.OrdinalIgnoreCase) ? highPerfGuid : null);
            if (target != null)
            {
                RunPowerCfg($"/SETACTIVE {target}");
                return true;
            }
        }
        catch { }
        return false;
    }

    private static string? _lastReleaseEtag;
    private static (bool HasUpdate, string LatestTag, string ReleaseUrl, string ZipDownloadUrl) _cachedUpdateResult =
        (false, string.Empty, string.Empty, string.Empty);

    public static async Task<(bool HasUpdate, string LatestTag, string ReleaseUrl, string ZipDownloadUrl)> CheckForGithubUpdateAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/Cat0rg/DesktopIsland-/releases/latest");
            req.Headers.UserAgent.ParseAdd("DesktopIsland/1.6");
            if (!string.IsNullOrEmpty(_lastReleaseEtag))
                req.Headers.TryAddWithoutValidation("If-None-Match", _lastReleaseEtag);

            using var resp = await Http.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.NotModified)
                return _cachedUpdateResult;
            if (!resp.IsSuccessStatusCode)
                return (false, string.Empty, string.Empty, string.Empty);

            if (resp.Headers.ETag != null)
                _lastReleaseEtag = resp.Headers.ETag.ToString();

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tag_name", out var tagEl))
                return (false, string.Empty, string.Empty, string.Empty);

            string tag = tagEl.GetString() ?? string.Empty;
            string cleanTag = tag.TrimStart('v', 'V');
            string htmlUrl = doc.RootElement.TryGetProperty("html_url", out var urlEl)
                ? (urlEl.GetString() ?? "https://github.com/Cat0rg/DesktopIsland-/releases/latest")
                : "https://github.com/Cat0rg/DesktopIsland-/releases/latest";

            string zipUrl = "https://github.com/Cat0rg/DesktopIsland-/releases/latest/download/DesktopIsland.zip";
            if (doc.RootElement.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsEl.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    string? dl = asset.TryGetProperty("browser_download_url", out var dlEl) ? dlEl.GetString() : null;
                    if (!string.IsNullOrEmpty(name) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(dl))
                    {
                        zipUrl = dl;
                        break;
                    }
                }
            }

            var currentVer = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 6, 0, 0);
            bool hasUpdate = false;
            if (Version.TryParse(cleanTag, out var remoteVer))
            {
                var normCur = new Version(currentVer.Major, currentVer.Minor, Math.Max(0, currentVer.Build));
                var normRem = new Version(remoteVer.Major, remoteVer.Minor, Math.Max(0, remoteVer.Build));
                hasUpdate = normRem > normCur;
            }

            _cachedUpdateResult = (hasUpdate, tag, htmlUrl, zipUrl);
            return _cachedUpdateResult;
        }
        catch { }
        return (false, string.Empty, string.Empty, string.Empty);
    }

    public static async Task<bool> DownloadAndApplyGithubUpdateAsync(string? zipUrl)
    {
        string url = string.IsNullOrWhiteSpace(zipUrl)
            ? "https://github.com/Cat0rg/DesktopIsland-/releases/latest/download/DesktopIsland.zip"
            : zipUrl;

        string workDir = Path.Combine(Path.GetTempPath(), "DesktopIsland_SelfUpdate");
        string zipFile = Path.Combine(workDir, "DesktopIsland.zip");
        string extractDir = Path.Combine(workDir, "unzipped");

        try
        {
            if (Directory.Exists(workDir))
                Directory.Delete(workDir, recursive: true);
            Directory.CreateDirectory(workDir);

            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.UserAgent.ParseAdd("DesktopIsland/1.6");
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) return false;
                await using var fs = new FileStream(zipFile, FileMode.Create, FileAccess.Write, FileShare.None);
                await resp.Content.CopyToAsync(fs);
            }

            var info = new FileInfo(zipFile);
            if (!info.Exists || info.Length < 1024 * 1024)
                return false;

            ZipFile.ExtractToDirectory(zipFile, extractDir, overwriteFiles: true);

            string? srcAppDir = null;
            string nestedApp = Path.Combine(extractDir, "App", "DesktopIsland.exe");
            string rootApp = Path.Combine(extractDir, "DesktopIsland.exe");
            if (File.Exists(nestedApp))
                srcAppDir = Path.GetDirectoryName(nestedApp);
            else if (File.Exists(rootApp))
                srcAppDir = extractDir;

            if (string.IsNullOrEmpty(srcAppDir))
                return false;

            string destAppDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string destExe = Path.Combine(destAppDir, "DesktopIsland.exe");
            int currentPid = Environment.ProcessId;
            string cmdPath = Path.Combine(Path.GetTempPath(), "DesktopIsland_ApplyUpdate.cmd");

            string script = $@"@echo off
chcp 65001 >nul
:waitloop
tasklist /FI ""PID eq {currentPid}"" 2>nul | find ""{currentPid}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    taskkill /F /PID {currentPid} >nul 2>&1
    goto :waitloop
)
xcopy /E /Y /I /Q ""{srcAppDir}\*"" ""{destAppDir}\"" >nul
rmdir /S /Q ""{workDir}"" >nul 2>&1
schtasks /Run /TN ""DesktopIsland_AutoStart"" >nul 2>&1
timeout /t 2 /nobreak >nul
tasklist /FI ""IMAGENAME eq DesktopIsland.exe"" 2>nul | find /I ""DesktopIsland.exe"" >nul
if errorlevel 1 (
    start """" /D ""{destAppDir}"" ""{destExe}""
)
del ""%~f0"" >nul 2>&1
";
            File.WriteAllText(cmdPath, script, new UTF8Encoding(false));

            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{cmdPath}\"\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            return true;
        }
        catch (Exception ex)
        {
            LoggerService.Error("Self-update download/apply failed.", ex);
            return false;
        }
    }

    public static void LockPc()
    {
        try { LockWorkStation(); } catch { }
    }

    public static void SleepPc()
    {
        try { SetSuspendState(false, false, false); } catch { }
    }

    public static void RebootPc()
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch { }
    }

    public static void SetDoNotDisturbEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings");
            key.SetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED", enabled ? 0 : 1, RegistryValueKind.DWord);
        }
        catch { }
    }

    public static (bool HasWifi, bool WifiOn, bool LanConnected, bool AirplaneOn, bool VpnActive, string VpnLabel) GetControlCenterNetworkStatus()
    {
        bool hasWifi = false, wifiOn = false, lanConnected = false, airplaneOn = false, vpnActive = false;
        string vpnLabel = "Выкл.";

        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                bool up = ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up;
                string nameDesc = $"{ni.Name} {ni.Description}";

                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
                {
                    hasWifi = true;
                    if (up) wifiOn = true;
                }
                else if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ppp ||
                         ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel ||
                         nameDesc.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
                         nameDesc.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase) ||
                         nameDesc.Contains("Amnezia", StringComparison.OrdinalIgnoreCase) ||
                         nameDesc.Contains("WARP", StringComparison.OrdinalIgnoreCase) ||
                         nameDesc.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                         nameDesc.Contains("Proton", StringComparison.OrdinalIgnoreCase) ||
                         nameDesc.Contains("Nord", StringComparison.OrdinalIgnoreCase) ||
                         nameDesc.Contains("VPN", StringComparison.OrdinalIgnoreCase))
                {
                    if (up)
                    {
                        vpnActive = true;
                        vpnLabel = "Рабочий";
                    }
                }
                else if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet && up)
                {
                    lanConnected = true;
                }
            }
        }
        catch { }

        try
        {
            using var radioKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\RadioManagement\SystemRadioState");
            if (radioKey?.GetValue(null) is int radioVal)
                airplaneOn = radioVal == 1;
        }
        catch { }

        return (hasWifi, wifiOn, lanConnected, airplaneOn, vpnActive, vpnLabel);
    }

    public static async Task<(bool ToggledWifi, string Message)> ToggleWifiOrNetworkAsync()
    {
        try
        {
            var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
            var wifi = System.Linq.Enumerable.FirstOrDefault(radios, r => r.Kind == Windows.Devices.Radios.RadioKind.WiFi);
            if (wifi != null)
            {
                bool nextOn = wifi.State != Windows.Devices.Radios.RadioState.On;
                var res = await wifi.SetStateAsync(nextOn ? Windows.Devices.Radios.RadioState.On : Windows.Devices.Radios.RadioState.Off);
                if (res == Windows.Devices.Radios.RadioAccessStatus.Allowed)
                    return (true, nextOn ? "Wi-Fi включён" : "Wi-Fi выключен");
            }
        }
        catch { }

        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true });
        }
        catch { }
        return (false, "Открыты параметры сети");
    }

    public static void OpenAirplaneSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:network-airplanemode") { UseShellExecute = true }); } catch { }
    }

    public static void ToggleOrOpenVpn()
    {
        try
        {
            string[] knownExes =
            {
                @"C:\Program Files\AmneziaVPN\AmneziaVPN.exe",
                @"C:\Program Files\WireGuard\wireguard.exe",
                @"C:\Program Files\Cloudflare\Cloudflare WARP\Cloudflare WARP.exe",
                @"C:\Program Files\OpenVPN Connect\OpenVPNConnect.exe",
                @"C:\Program Files\Proton\VPN\ProtonVPN.Launcher.exe"
            };
            foreach (var exe in knownExes)
            {
                if (System.IO.File.Exists(exe))
                {
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                    return;
                }
            }
            Process.Start(new ProcessStartInfo("ms-settings:network-vpn") { UseShellExecute = true });
        }
        catch { }
    }

    public static void TurnOffMonitors()
    {
        try { SendMessage(HWND_BROADCAST, WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(2)); } catch { }
    }

    public static void ShutdownPc() => HardwareAndUtilityService.ShutdownPc();

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

            var weatherUrl = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}&longitude={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}&current=temperature_2m,apparent_temperature,relative_humidity_2m,wind_speed_10m,weather_code&hourly=temperature_2m,weather_code,precipitation_probability&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=6";
            var wJson = await Http.GetStringAsync(weatherUrl);
            using var wDoc = JsonDocument.Parse(wJson);
            var cur = wDoc.RootElement.GetProperty("current");
            double temp = cur.GetProperty("temperature_2m").GetDouble();
            double feelsLike = cur.TryGetProperty("apparent_temperature", out var flEl) ? flEl.GetDouble() : temp;
            double windKmh = cur.TryGetProperty("wind_speed_10m", out var wEl) ? wEl.GetDouble() : 0;
            int humidity = cur.TryGetProperty("relative_humidity_2m", out var hEl) ? hEl.GetInt32() : 0;
            int code = cur.GetProperty("weather_code").GetInt32();
            string curTimeStr = cur.TryGetProperty("time", out var tEl) ? (tEl.GetString() ?? string.Empty) : string.Empty;
            DateTime.TryParse(curTimeStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var curLocalTime);

            var info = new WeatherInfo
            {
                City = cityName,
                TempC = temp,
                FeelsLikeC = feelsLike,
                WindKmh = windKmh,
                HumidityPercent = humidity,
                WmoCode = code,
                ConditionText = MapWmoCondition(code),
                IconGlyph = MapWmoCode(code)
            };

            try
            {
                bool currentlyWet = code >= 51;
                if (wDoc.RootElement.TryGetProperty("hourly", out var hourly))
                {
                    var hTimes = hourly.GetProperty("time");
                    var hTemps = hourly.GetProperty("temperature_2m");
                    var hCodes = hourly.GetProperty("weather_code");
                    var hProbs = hourly.GetProperty("precipitation_probability");
                    int count = Math.Min(Math.Min(hTimes.GetArrayLength(), hTemps.GetArrayLength()), Math.Min(hCodes.GetArrayLength(), hProbs.GetArrayLength()));

                    int startIdx = 0;
                    if (curLocalTime != default)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            if (DateTime.TryParse(hTimes[i].GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var ht)
                                && ht >= curLocalTime.AddMinutes(-30))
                            {
                                startIdx = i;
                                break;
                            }
                        }
                    }

                    if (!currentlyWet && startIdx + 1 < count)
                    {
                        int p1 = hProbs[startIdx].GetInt32();
                        int p2 = hProbs[startIdx + 1].GetInt32();
                        int maxProb = Math.Max(p1, p2);
                        if (maxProb >= 60)
                            info.RainAlertText = $"В ближайший час возможны осадки ({maxProb}%)";
                    }

                    for (int i = startIdx; i < Math.Min(startIdx + 6, count); i++)
                    {
                        string? ts = hTimes[i].GetString();
                        string hLabel = DateTime.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var hdt)
                            ? (i == startIdx ? "Сейчас" : hdt.ToString("HH:00"))
                            : "+1ч";
                        int hc = hCodes[i].GetInt32();
                        info.Hourly.Add(new WeatherHourItem
                        {
                            TimeLabel = hLabel,
                            TempC = hTemps[i].GetDouble(),
                            WmoCode = hc,
                            PrecipProb = hProbs[i].GetInt32(),
                            IconGlyph = MapWmoCode(hc)
                        });
                    }
                }

                if (wDoc.RootElement.TryGetProperty("daily", out var daily))
                {
                    var dTimes = daily.GetProperty("time");
                    var dCodes = daily.GetProperty("weather_code");
                    var dMin = daily.GetProperty("temperature_2m_min");
                    var dMax = daily.GetProperty("temperature_2m_max");
                    int dCount = Math.Min(5, Math.Min(dTimes.GetArrayLength(), Math.Min(dCodes.GetArrayLength(), Math.Min(dMin.GetArrayLength(), dMax.GetArrayLength()))));

                    var rawDays = new List<WeatherDayItem>();
                    double globalMin = double.MaxValue, globalMax = double.MinValue;
                    var ruCulture = new System.Globalization.CultureInfo("ru-RU");

                    for (int i = 0; i < dCount; i++)
                    {
                        double minT = dMin[i].GetDouble();
                        double maxT = dMax[i].GetDouble();
                        globalMin = Math.Min(globalMin, minT);
                        globalMax = Math.Max(globalMax, maxT);

                        string dayLabel = i == 0 ? "Сегодня" : "+1д";
                        if (i > 0 && DateTime.TryParse(dTimes[i].GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var ddt))
                        {
                            dayLabel = ruCulture.DateTimeFormat.GetAbbreviatedDayName(ddt.DayOfWeek);
                            if (dayLabel.Length > 0)
                                dayLabel = char.ToUpper(dayLabel[0], ruCulture) + dayLabel[1..];
                        }

                        int dc = dCodes[i].GetInt32();
                        rawDays.Add(new WeatherDayItem
                        {
                            DayLabel = dayLabel,
                            WmoCode = dc,
                            IconGlyph = MapWmoCode(dc),
                            MinTempC = minT,
                            MaxTempC = maxT
                        });
                    }

                    double span = Math.Max(1.0, globalMax - globalMin);
                    const double trackWidth = 132.0;
                    foreach (var d in rawDays)
                    {
                        double startFrac = (d.MinTempC - globalMin) / span;
                        double widthFrac = (d.MaxTempC - d.MinTempC) / span;
                        d.BarLeftPx = Math.Round(startFrac * trackWidth, 1);
                        d.BarWidthPx = Math.Max(14.0, Math.Round(widthFrac * trackWidth, 1));
                    }
                    info.Daily = rawDays;
                }
            }
            catch { }

            return info;
        }
        catch
        {
            return null;
        }
    }

    private static string MapWmoCondition(int code) => code switch
    {
        0 => "Ясно",
        1 => "Почти ясно",
        2 => "Переменно",
        3 => "Пасмурно",
        45 or 48 => "Туман",
        >= 51 and <= 57 => "Морось",
        >= 61 and <= 67 => "Дождь",
        >= 71 and <= 77 => "Снег",
        >= 80 and <= 82 => "Ливень",
        >= 85 and <= 86 => "Снегопад",
        >= 95 => "Гроза",
        _ => "Облачно"
    };

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
