using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DesktopIsland.Services;

public static class HardwareAndUtilityService
{
    private const int MONITOR_DEFAULTTOPRIMARY = 1;
    private const byte VK_MENU = 0x12;
    private const byte VK_LWIN = 0x5B;
    private const byte VK_F10 = 0x79;
    private const byte VK_G = 0x47;
    private const byte VK_CAPITAL = 0x14;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_BATTERY_INFORMATION
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public ulong GamepadLo;
        public uint GamepadHi;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
    private static extern int XInputGetBatteryInformation(uint dwUserIndex, byte devType, out XINPUT_BATTERY_INFORMATION pBatteryInformation);

    private const uint Th32csSnapProcess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static Dictionary<string, double> _usdRates = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _ratesFetchedUtc = DateTime.MinValue;
    private static DateTime _lastWmiBrightnessAt = DateTime.MinValue;

    public static int? GetMonitorBrightnessPercent(IntPtr hwnd)
    {
        try
        {
            var hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY);
            if (hMon != IntPtr.Zero && GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) && count > 0)
            {
                var arr = new PHYSICAL_MONITOR[count];
                if (GetPhysicalMonitorsFromHMONITOR(hMon, count, arr))
                {
                    try
                    {
                        if (GetMonitorBrightness(arr[0].hPhysicalMonitor, out uint min, out uint cur, out uint max) && max > min)
                            return Math.Clamp((int)Math.Round((cur - min) * 100.0 / (max - min)), 0, 100);
                    }
                    finally
                    {
                        DestroyPhysicalMonitors(count, arr);
                    }
                }
            }
        }
        catch { }
        return null;
    }

    public static bool SetMonitorBrightnessPercent(IntPtr hwnd, int percent)
    {
        int clamped = Math.Clamp(percent, 0, 100);
        try
        {
            var hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY);
            if (hMon != IntPtr.Zero && GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) && count > 0)
            {
                var arr = new PHYSICAL_MONITOR[count];
                if (GetPhysicalMonitorsFromHMONITOR(hMon, count, arr))
                {
                    bool anySet = false;
                    try
                    {
                        foreach (var pm in arr)
                        {
                            if (GetMonitorBrightness(pm.hPhysicalMonitor, out uint min, out _, out uint max) && max > min)
                            {
                                uint target = (uint)Math.Round(min + (max - min) * (clamped / 100.0));
                                if (SetMonitorBrightness(pm.hPhysicalMonitor, target))
                                    anySet = true;
                            }
                            else
                            {
                                if (SetMonitorBrightness(pm.hPhysicalMonitor, (uint)clamped))
                                    anySet = true;
                            }
                        }
                    }
                    finally
                    {
                        DestroyPhysicalMonitors(count, arr);
                    }
                    if (anySet) return true;
                }
            }
        }
        catch { }

        // Fallback for laptop panels via WMI PowerShell in background (throttled)
        if ((DateTime.UtcNow - _lastWmiBrightnessAt).TotalMilliseconds < 220)
            return true;
        _lastWmiBrightnessAt = DateTime.UtcNow;

        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -WindowStyle Hidden -Command \"(Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods | Invoke-CimMethod -MethodName WmiSetBrightness -Arguments @{{Timeout=1;Brightness={clamped}}})\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void OpenNightLightSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:nightlight") { UseShellExecute = true }); } catch { }
    }

    public static void ShutdownPc()
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 2")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch { }
    }

    public static string TriggerReplayClip()
    {
        try
        {
            bool hasNvidia = Process.GetProcessesByName("nvcontainer").Length > 0
                          || Process.GetProcessesByName("NVIDIA Overlay").Length > 0;
            if (hasNvidia)
            {
                // Alt + F10 (ShadowPlay Instant Replay)
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                keybd_event(VK_F10, 0, 0, UIntPtr.Zero);
                keybd_event(VK_F10, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                return "ShadowPlay клип (Alt+F10)";
            }

            // Win + Alt + G (Windows Game Bar Record That)
            keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_G, 0, 0, UIntPtr.Zero);
            keybd_event(VK_G, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            return "Игровой клип (Win+Alt+G)";
        }
        catch
        {
            return "Запрос клипа отправлен";
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _layoutCodeCache = new();

    public static string GetForegroundKeyboardLayoutCode()
    {
        try
        {
            var fg = GetForegroundWindow();
            uint tid = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
            if (tid == 0) return string.Empty;
            IntPtr hkl = GetKeyboardLayout(tid);
            if (hkl == IntPtr.Zero) return string.Empty;
            int langId = unchecked((short)(hkl.ToInt64() & 0xFFFF));
            if (langId == 0) return string.Empty;
            if (_layoutCodeCache.TryGetValue(langId, out var cached))
                return cached;

            var culture = CultureInfo.GetCultureInfo(langId);
            string code = culture.TwoLetterISOLanguageName.ToUpperInvariant();
            if (code == "IV") code = string.Empty;
            _layoutCodeCache[langId] = code;
            return code;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static bool IsCapsLockOn()
    {
        try { return (GetKeyState(VK_CAPITAL) & 0x0001) != 0; }
        catch { return false; }
    }

    private static long _lastProcessScanTickMs;
    private static bool _cachedRecorderActive;
    private static bool _cachedDiscordRunning;
    private static readonly HashSet<string> RecorderExeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "obs64.exe", "obs32.exe", "streamlabs.exe", "xsplit.core.exe", "bdcam.exe", "action_x64.exe", "medal.exe"
    };

    private static void EnsureProcessPresenceSnapshot()
    {
        long now = Environment.TickCount64;
        if (_lastProcessScanTickMs != 0 && now - _lastProcessScanTickMs < 3500)
            return;

        bool rec = false;
        bool discord = false;
        IntPtr snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snap != IntPtr.Zero && snap != new IntPtr(-1))
        {
            try
            {
                var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (Process32First(snap, ref pe))
                {
                    do
                    {
                        var exe = pe.szExeFile;
                        if (!string.IsNullOrEmpty(exe))
                        {
                            if (!rec && RecorderExeNames.Contains(exe))
                                rec = true;
                            if (!discord && exe.Equals("Discord.exe", StringComparison.OrdinalIgnoreCase))
                                discord = true;
                            if (rec && discord)
                                break;
                        }
                    }
                    while (Process32Next(snap, ref pe));
                }
            }
            finally
            {
                CloseHandle(snap);
            }
        }
        _cachedRecorderActive = rec;
        _cachedDiscordRunning = discord;
        _lastProcessScanTickMs = now;
    }

    public static bool IsRecordingSoftwareActive()
    {
        EnsureProcessPresenceSnapshot();
        return _cachedRecorderActive;
    }

    private static string? _cachedGpuName;

    public static string GetPrimaryGpuName()
    {
        if (!string.IsNullOrEmpty(_cachedGpuName))
            return _cachedGpuName;

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000");
            if (key?.GetValue("DriverDesc") is string desc && !string.IsNullOrWhiteSpace(desc))
            {
                string clean = desc.Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase)
                                   .Replace("AMD ", "", StringComparison.OrdinalIgnoreCase)
                                   .Trim()
                                   .ToUpperInvariant();
                _cachedGpuName = clean;
                return _cachedGpuName;
            }
        }
        catch { }
        _cachedGpuName = "ВИДЕОКАРТА";
        return _cachedGpuName;
    }

    public static (bool IsRunning, bool InVoice) GetDiscordVoiceStatus(string? preSampledActiveMic = null, bool usePreSampled = false)
    {
        try
        {
            EnsureProcessPresenceSnapshot();
            if (!_cachedDiscordRunning)
                return (false, false);

            var activeMic = usePreSampled ? preSampledActiveMic : CoreAudioService.GetActiveCaptureProcessName();
            bool inVoice = !string.IsNullOrEmpty(activeMic) && activeMic.Contains("Discord", StringComparison.OrdinalIgnoreCase);
            return (true, inVoice);
        }
        catch
        {
            return (false, false);
        }
    }

    public static List<(int Index, int BatteryPercent, bool IsWired)> GetConnectedGamepads()
    {
        var list = new List<(int, int, bool)>();
        try
        {
            for (uint i = 0; i < 4; i++)
            {
                if (XInputGetState(i, out _) == 0)
                {
                    int pct = 100;
                    bool wired = false;
                    if (XInputGetBatteryInformation(i, 0 /* BATTERY_DEVTYPE_GAMEPAD */, out var bat) == 0)
                    {
                        if (bat.BatteryType == 1) wired = true; // WIRED
                        pct = bat.BatteryLevel switch
                        {
                            0 => 10, // EMPTY
                            1 => 35, // LOW
                            2 => 70, // MEDIUM
                            _ => 100 // FULL
                        };
                    }
                    list.Add(((int)i + 1, pct, wired));
                }
            }
        }
        catch { }
        return list;
    }

    public static async Task EnsureExchangeRatesAsync()
    {
        if (_usdRates.Count > 0 && (DateTime.UtcNow - _ratesFetchedUtc).TotalHours < 12)
            return;

        try
        {
            var json = await Http.GetStringAsync("https://open.er-api.com/v6/latest/USD");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("rates", out var rates))
            {
                var next = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in rates.EnumerateObject())
                    if (prop.Value.TryGetDouble(out double r))
                        next[prop.Name] = r;
                if (next.Count > 0)
                {
                    _usdRates = next;
                    _ratesFetchedUtc = DateTime.UtcNow;
                }
            }
        }
        catch { }
    }

    public static bool TryEvaluateExpression(string raw, string targetCurrency, out string formattedResult)
    {
        formattedResult = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string s = raw.Trim();

        // Currency patterns like "$49.99", "49.99$", "120 EUR", "500 uah to usd", "100$"
        var curMatch = Regex.Match(s, @"^(?:([$€₴₽zł])\s*(\d+(?:[.,]\d+)?)|(\d+(?:[.,]\d+)?)\s*([$€₴₽]|usd|eur|uah|rub|pln|грн|руб|zl)(?:\s+(?:in|to|в)\s+(usd|eur|uah|rub|pln|грн|руб))?)$", RegexOptions.IgnoreCase);
        if (curMatch.Success && _usdRates.Count > 0)
        {
            string sym = curMatch.Groups[1].Success ? curMatch.Groups[1].Value : curMatch.Groups[4].Value;
            string numStr = (curMatch.Groups[2].Success ? curMatch.Groups[2].Value : curMatch.Groups[3].Value).Replace(',', '.');
            if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double amount))
            {
                string srcCode = NormalizeCurrency(sym);
                string dstCode = curMatch.Groups[5].Success
                    ? NormalizeCurrency(curMatch.Groups[5].Value)
                    : (string.Equals(srcCode, targetCurrency, StringComparison.OrdinalIgnoreCase) ? "USD" : targetCurrency);

                if (_usdRates.TryGetValue(srcCode, out double rSrc) && _usdRates.TryGetValue(dstCode, out double rDst) && rSrc > 0)
                {
                    double converted = amount / rSrc * rDst;
                    formattedResult = $"{amount:G} {srcCode} ≈ {converted:N2} {dstCode}";
                    return true;
                }
            }
        }

        // Pure math expression: e.g. "1250*0.85", "(40+20)/3"
        if (s.Length <= 48 && Regex.IsMatch(s, @"^[\d\s\+\-\*\/\.\,\(\)]+$") && Regex.IsMatch(s, @"[\+\-\*\/]"))
        {
            try
            {
                string expr = s.Replace(',', '.').Replace("=", "").Trim();
                using var dt = new DataTable();
                var val = dt.Compute(expr, string.Empty);
                if (val != null && double.TryParse(Convert.ToString(val, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double res)
                    && !double.IsNaN(res) && !double.IsInfinity(res))
                {
                    formattedResult = $"= {Math.Round(res, 4).ToString("G", CultureInfo.InvariantCulture)}";
                    return true;
                }
            }
            catch { }
        }
        return false;
    }

    private static string NormalizeCurrency(string token) => token.Trim().ToLowerInvariant() switch
    {
        "$" or "usd" => "USD",
        "€" or "eur" => "EUR",
        "₴" or "uah" or "грн" => "UAH",
        "₽" or "rub" or "руб" => "RUB",
        "zł" or "zl" or "pln" => "PLN",
        _ => token.Trim().ToUpperInvariant()
    };
}
