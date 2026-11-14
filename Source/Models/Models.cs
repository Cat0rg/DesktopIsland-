using System;
using System.Collections.Generic;
using MediaColor = System.Windows.Media.Color;

namespace DesktopIsland.Models;

public class IslandDockProfile
{
    public string DockEdge { get; set; } = "Top";
    public double HorizontalOffsetPx { get; set; } = 0;
    public double VerticalOffsetPx { get; set; } = 0;
}

public class PinnedApp
{
    public string Name { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
}

public class AppSettings
{
    public List<PinnedApp> PinnedApps { get; set; } = new();
    public List<ClipboardEntry> PinnedSnippets { get; set; } = new();

    public bool EnableAnimations { get; set; } = true;
    public int AnimationDurationMs { get; set; } = 210;
    public bool ExpandOnHover { get; set; } = true;
    public int HoverOutDelayMs { get; set; } = 360;
    public int MonitorIndex { get; set; } = 0;
    public int PreferredRecordMonitorIndex { get; set; } = -1; // -1 = авто (основной / текущий экран)
    public int TopOffsetPx { get; set; } = 6;
    public double HorizontalOffsetPx { get; set; } = 0;
    public double VerticalOffsetPx { get; set; } = 0;
    public string DockEdge { get; set; } = "Top"; // Top, Left, Right
    public Dictionary<string, IslandDockProfile> ProcessDockProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> GameAudioProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool DockToTopEdge { get; set; } = true;
    public double CapsuleOpacity { get; set; } = 0.96;
    public string AccentTheme { get; set; } = "Sky"; // Sky, Emerald, Violet, Amber, Rose
    public string BorderStyle { get; set; } = "Glass"; // Glass, Neon, Minimal
    public string PreferredMediaPlayer { get; set; } = "Auto"; // Auto, Spotify, SoundCloud
    public bool MusicFilterOnlyMusicApps { get; set; } = true;
    public bool SuperMiniOnIdle { get; set; } = true;
    public bool ShowLayoutAndCapsIndicator { get; set; } = true;
    public bool ShowReplayButtonInGame { get; set; } = true;
    public bool StreamPrivacyAuto { get; set; } = true;
    public string PreferredCurrency { get; set; } = "UAH";

    public bool EnableSystemMonitor { get; set; } = true;
    public bool EnableDiskMonitor { get; set; } = true;
    public bool EnableNetworkMonitor { get; set; } = true;
    public bool EnableClipboardHistory { get; set; } = true;
    public int ClipboardMaxItems { get; set; } = 20;
    public int PollingIntervalMs { get; set; } = 2000;

    public bool CompactShowCpuRam { get; set; } = true;
    public bool CompactShowFps { get; set; } = true;
    public bool CompactShowPing { get; set; } = true;
    public bool CompactShowNetwork { get; set; } = false;
    public bool CompactShowMedia { get; set; } = true;
    public bool CompactShowWeather { get; set; } = true;

    public bool GameModeAutoProfile { get; set; } = true;
    public bool GameClickThrough { get; set; } = true;
    public bool GameAutoBoost { get; set; } = true;
    public bool GameShowGpuTemp { get; set; } = true;
    public bool GameShowMicMute { get; set; } = true;
    public bool DimOnFullscreenVideo { get; set; } = true;
    public bool AutoOptimizeRamOnThreshold { get; set; } = false;
    public int RamThresholdPercent { get; set; } = 85;
    public string WeatherCity { get; set; } = string.Empty;

    public bool AlwaysOnTop { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool ConfirmControversialCleanup { get; set; } = true;
}

public class ClipboardEntry
{
    public string Text { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsPinned { get; set; }

    public string Preview
    {
        get
        {
            var singleLine = Text.Replace("\r", " ").Replace("\n", " ").Trim();
            return singleLine.Length > 54 ? singleLine[..54] + "..." : singleLine;
        }
    }

    public string TimeLabel => IsPinned ? "📌 Закреп." : Timestamp.ToString("HH:mm:ss");
}

public class SystemMetrics
{
    public double CpuPercent { get; set; }
    public double RamTotalGb { get; set; }
    public double RamUsedGb { get; set; }
    public double RamFreeGb { get; set; }
    public double RamPercent { get; set; }
    public double NetDownBytesPerSec { get; set; }
    public double NetUpBytesPerSec { get; set; }
    public double ActiveFps { get; set; }
    public uint ActivePid { get; set; }
    public string ActiveWindowTitle { get; set; } = string.Empty;
    public int PingMs { get; set; } = -1;
    public int PingJitterMs { get; set; }
    public int PacketLossPercent { get; set; }
    public double? GpuPercent { get; set; }
    public double? GpuTempC { get; set; }
}

public class ScreenTimeItem
{
    public string ProcessName { get; set; } = string.Empty;
    public bool IsGame { get; set; }
    public TimeSpan Duration { get; set; }
    public double SharePercent { get; set; }
    public double? AvgFps { get; set; }
    public double? LowFps1Percent { get; set; }
    public string IconLabel => IsGame ? "🎮" : "🖥";
    public string DurationText => Duration.TotalHours >= 1
        ? $"{(int)Duration.TotalHours}ч {Duration.Minutes:D2}м"
        : (Duration.TotalMinutes >= 1 ? $"{(int)Duration.TotalMinutes}м" : $"{(int)Duration.TotalSeconds}с");
    public string SubLabel
    {
        get
        {
            if (AvgFps.HasValue && AvgFps.Value >= 1)
            {
                string low = LowFps1Percent.HasValue && LowFps1Percent.Value >= 1
                    ? $" · 1% low {LowFps1Percent.Value:F0}"
                    : string.Empty;
                return $"ср. {AvgFps.Value:F0}{low} FPS · {DurationText}";
            }
            return DurationText;
        }
    }
}

public class DriveMetric
{
    public string Name { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public double TotalGb { get; set; }
    public double UsedGb { get; set; }
    public double FreeGb { get; set; }
    public double UsedPercent { get; set; }
}

public class CleanupCategory
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public bool IsSelected { get; set; } = true;
    public bool RequiresConfirmation { get; set; } = false;

    public string SizeFormatted => FormatBytes(SizeBytes);

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F2} ГБ";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024.0 * 1024):F1} МБ";
        if (bytes >= 1024L)
            return $"{bytes / 1024.0:F0} КБ";
        return $"{bytes} Б";
    }
}

public class CleanupResult
{
    public long FreedBytes { get; set; }
    public int DeletedFilesCount { get; set; }
    public int SkippedCount { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public class MemoryOptimizationResult
{
    public double BeforeUsedGb { get; set; }
    public double AfterUsedGb { get; set; }
    public double FreedGb { get; set; }
    public uint BeforePercent { get; set; }
    public uint AfterPercent { get; set; }
    public int TrimmedProcessesCount { get; set; }
}

public class MediaTrackInfo
{
    public bool HasSession { get; set; }
    public bool IsPlaying { get; set; }
    public string Title { get; set; } = "Ничего не воспроизводится";
    public string Artist { get; set; } = string.Empty;
    public string SourceApp { get; set; } = string.Empty;
    public string SourceLabel { get; set; } = string.Empty;
    public bool IsDedicatedMusicSource { get; set; }
    public bool HasTimeline { get; set; }
    public double ProgressRatio { get; set; }
    public System.Windows.Media.ImageSource? Thumbnail { get; set; }
    public MediaColor? DominantColor { get; set; }
}

public class AudioSessionItem
{
    public int Pid { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public int VolumePercent { get; set; }
    public bool IsMuted { get; set; }
    public string MuteGlyph => IsMuted ? "🔇" : "🔊";
}

public class AudioDeviceItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool IsHeadphones { get; set; }
    public string BadgeText => IsDefault ? "Активно" : "Выбрать";
    public string IconGlyph => IsHeadphones ? "🎧" : "🔊";
}

public class BluetoothDeviceItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsConnected { get; set; }
    public int? BatteryPercent { get; set; }
    public string StatusLabel
    {
        get
        {
            var status = IsConnected ? "Подключено" : "Не подключено";
            return BatteryPercent.HasValue ? $"{status} • 🔋 {BatteryPercent}%" : status;
        }
    }
    public string ActionLabel => IsConnected ? "Откл." : "Подкл.";
}

public class ProcessMetricItem
{
    public int Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public double MemoryMb { get; set; }
    public string MemoryFormatted => MemoryMb >= 1024 ? $"{MemoryMb / 1024.0:F1} ГБ" : $"{MemoryMb:F0} МБ";
}

public class RecentDownloadItem
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string SizeFormatted { get; set; } = string.Empty;
    public string TimeLabel { get; set; } = string.Empty;
}

public class PrivacyStatusInfo
{
    public string? MicApp { get; set; }
    public string? CameraApp { get; set; }
    public bool IsMicActive => !string.IsNullOrEmpty(MicApp);
    public bool IsCameraActive => !string.IsNullOrEmpty(CameraApp);
}

public class WeatherInfo
{
    public string City { get; set; } = string.Empty;
    public double TempC { get; set; }
    public string IconGlyph { get; set; } = "☀";
    public string ShortText => $"{IconGlyph} {Math.Round(TempC):+0;-0;0}°";
    public string? RainAlertText { get; set; }
}

public class DropShelfItem
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsFile { get; set; }
    public string IconGlyph => IsFile ? (System.IO.Directory.Exists(FullPath) ? "📁" : "📄") : "📝";
}

public static class ThemeHelper
{
    public static (MediaColor Primary, MediaColor Bright) GetAccentColors(string? theme) => theme switch
    {
        "Emerald" => (MediaColor.FromRgb(16, 185, 129), MediaColor.FromRgb(52, 211, 153)),
        "Violet" => (MediaColor.FromRgb(139, 92, 246), MediaColor.FromRgb(167, 139, 250)),
        "Amber" => (MediaColor.FromRgb(245, 158, 11), MediaColor.FromRgb(251, 191, 36)),
        "Rose" => (MediaColor.FromRgb(244, 63, 94), MediaColor.FromRgb(251, 113, 133)),
        _ => (MediaColor.FromRgb(14, 165, 233), MediaColor.FromRgb(56, 189, 248))
    };
}
