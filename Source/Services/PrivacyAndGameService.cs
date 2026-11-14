using System;
using System.IO;
using System.Runtime.InteropServices;
using DesktopIsland.Models;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DesktopIsland.Services;

public static class PrivacyAndGameService
{
    private enum QUERY_USER_NOTIFICATION_STATE
    {
        QUNS_NOT_PRESENT = 1,
        QUNS_BUSY = 2,
        QUNS_RUNNING_D3D_FULL_SCREEN = 3,
        QUNS_PRESENTATION_MODE = 4,
        QUNS_ACCEPTS_NOTIFICATIONS = 5,
        QUNS_QUIET_TIME = 6,
        QUNS_APP = 7
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE pquns);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    public static PrivacyStatusInfo GetActiveMicAndCamera()
    {
        // Primary mic truth: active WASAPI audio capture session (prevents stale Windows Registry ghosts)
        string? liveMic = CoreAudioService.GetActiveCaptureProcessName();
        if (string.IsNullOrEmpty(liveMic))
            liveMic = FindActiveCapabilityApp(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone", verifyRunningForNonPackaged: true, skipNonPackagedIfNoLiveAudio: true);

        var info = new PrivacyStatusInfo
        {
            MicApp = liveMic,
            CameraApp = FindActiveCapabilityApp(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam", verifyRunningForNonPackaged: true, skipNonPackagedIfNoLiveAudio: false)
        };
        return info;
    }

    private static string? FindActiveCapabilityApp(string regSubKey, bool verifyRunningForNonPackaged, bool skipNonPackagedIfNoLiveAudio)
    {
        try
        {
            using var baseKey = Registry.CurrentUser.OpenSubKey(regSubKey);
            if (baseKey == null) return null;

            foreach (var name in baseKey.GetSubKeyNames())
            {
                if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    if (skipNonPackagedIfNoLiveAudio) continue;
                    using var np = baseKey.OpenSubKey("NonPackaged");
                    if (np == null) continue;
                    foreach (var npName in np.GetSubKeyNames())
                    {
                        using var sub = np.OpenSubKey(npName);
                        if (IsStopZero(sub))
                        {
                            var friendly = ExtractFriendlyName(npName);
                            if (verifyRunningForNonPackaged && !IsProcessCurrentlyRunning(friendly))
                                continue;
                            return friendly;
                        }
                    }
                }
                else
                {
                    using var sub = baseKey.OpenSubKey(name);
                    if (IsStopZero(sub))
                        return ExtractFriendlyName(name);
                }
            }
        }
        catch { }
        return null;
    }

    private static bool IsProcessCurrentlyRunning(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName(processName);
            bool alive = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            return alive;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsStopZero(RegistryKey? key)
    {
        if (key == null) return false;
        var val = key.GetValue("LastUsedTimeStop");
        return val is long l && l == 0 && (key.GetValue("LastUsedTimeStart") is long s && s > 0);
    }

    private static string ExtractFriendlyName(string raw)
    {
        if (raw.Contains('#'))
        {
            var last = raw.Split('#')[^1];
            return Path.GetFileNameWithoutExtension(last);
        }
        var underscore = raw.IndexOf('_');
        return underscore > 0 ? raw[..underscore] : raw;
    }

    public static (bool IsFullscreenGame, bool IsFullscreenVideo) DetectFullscreenContext(bool isMediaPlaying, int ownPid)
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return (false, false);

            GetWindowThreadProcessId(fg, out uint fgPid);
            if (fgPid == 0 || fgPid == (uint)ownPid) return (false, false);

            string procName = string.Empty;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById((int)fgPid);
                procName = p.ProcessName;
            }
            catch { }

            if (procName.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
                procName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase))
            {
                return (false, false);
            }

            if (SHQueryUserNotificationState(out var state) == 0 &&
                state == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN)
            {
                return (true, false);
            }

            if (GetWindowRect(fg, out var r))
            {
                var scr = Forms.Screen.FromHandle(fg);
                bool coversMonitor = r.Left <= scr.Bounds.Left + 2 && r.Top <= scr.Bounds.Top + 2 &&
                                     r.Right >= scr.Bounds.Right - 2 && r.Bottom >= scr.Bounds.Bottom - 2;
                if (coversMonitor)
                {
                    bool isBrowserOrPlayer = procName.Contains("chrome", StringComparison.OrdinalIgnoreCase)
                                          || procName.Contains("msedge", StringComparison.OrdinalIgnoreCase)
                                          || procName.Contains("firefox", StringComparison.OrdinalIgnoreCase)
                                          || procName.Contains("opera", StringComparison.OrdinalIgnoreCase)
                                          || procName.Contains("brave", StringComparison.OrdinalIgnoreCase)
                                          || procName.Contains("vlc", StringComparison.OrdinalIgnoreCase)
                                          || procName.Contains("mpc", StringComparison.OrdinalIgnoreCase)
                                          || procName.Contains("potplayer", StringComparison.OrdinalIgnoreCase)
                                          || procName.Contains("mpv", StringComparison.OrdinalIgnoreCase)
                                          || procName.Contains("Video", StringComparison.OrdinalIgnoreCase);

                    // Dim ONLY on actual browser/video player fullscreen; never dim over games even if background music plays!
                    if (isBrowserOrPlayer && isMediaPlaying)
                        return (false, true);

                    if (!isBrowserOrPlayer)
                        return (true, false);
                }
            }
        }
        catch { }
        return (false, false);
    }
}
