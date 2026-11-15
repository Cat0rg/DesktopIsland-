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
    public bool AutoOptimizeRamOnThreshold { get; set; } = true;
    public int RamThresholdPercent { get; set; } = 80;
    public int ConfigRevision { get; set; } = 0;
    public string WeatherCity { get; set; } = string.Empty;

    public bool AlwaysOnTop { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool ConfirmControversialCleanup { get; set; } = true;

    public bool UseHomeAppsDock { get; set; } = true;
    public string TranslateDirection { get; set; } = "Auto"; // Auto, EnToRu, RuToEn
    public List<string> HomeWidgetOrder { get; set; } = new() { "QuickTools", "MusicWeather", "HomeDock", "FileShelf", "Notifications" };
    public List<string> HiddenHomeWidgets { get; set; } = new();
    public List<QuickNoteItem> QuickNotes { get; set; } = new();
}

public class QuickNoteItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Text { get; set; } = string.Empty;
    public bool IsDone { get; set; }
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

    public string PercentLabel => $"{UsedPercent:F0}%";
    public string SpaceSummary => $"{FreeGb:F0} / {TotalGb:F0} ГБ своб.";
    public string AccentHex => UsedPercent >= 90 ? "#EF4444" : (UsedPercent >= 78 ? "#F59E0B" : "#38BDF8");
    public string BadgeBgHex => UsedPercent >= 90 ? "#2A1518" : (UsedPercent >= 78 ? "#292011" : "#132433");
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

    public string AccentHex => Id switch
    {
        "user_temp" => "#38BDF8",
        "win_temp" => "#A78BFA",
        "thumbs" => "#34D399",
        "wer" => "#FB7185",
        "shader_cache" => "#FBBF24",
        "recycle_bin" => "#F97316",
        _ => "#60A5FA"
    };

    public string AccentBgHex => Id switch
    {
        "user_temp" => "#12263A",
        "win_temp" => "#211B38",
        "thumbs" => "#122B22",
        "wer" => "#33161E",
        "shader_cache" => "#312310",
        "recycle_bin" => "#331E12",
        _ => "#192234"
    };

    public string IconData => Id switch
    {
        "user_temp" => "M 4,7 H 10 L 12,9 H 20 V 18 A 2,2 0 0 1 18,20 H 6 A 2,2 0 0 1 4,18 Z M 9.5,14 L 14.5,14",
        "win_temp" => "M 4,4 H 11 V 11 H 4 Z M 13,4 H 20 V 11 H 13 Z M 4,13 H 11 V 20 H 4 Z M 13,13 H 20 V 20 H 13 Z",
        "thumbs" => "M 4,5 H 20 V 19 H 4 Z M 8.5,10.5 A 1.5,1.5 0 1 1 8.5,7.5 A 1.5,1.5 0 1 1 8.5,10.5 M 5,17 L 10,12 L 13,15 L 16,13 L 19,17",
        "wer" => "M 12,3 L 21,19 H 3 Z M 12,10 V 14 M 12,16.8 V 17",
        "shader_cache" => "M 13,2 L 4,14 H 11 L 9,22 L 20,10 H 13 Z",
        "recycle_bin" => "M 4,7 H 20 M 9,7 V 4 H 15 V 7 M 6,7 L 7,20 H 17 L 18,7 M 10,11 V 16 M 14,11 V 16",
        _ => "M 5,6 H 19 V 18 H 5 Z"
    };

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
    public double PositionSeconds { get; set; }
    public double DurationSeconds { get; set; }
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
    public string StatusDotHex => IsConnected ? "#22C55E" : "#52525B";
}

public class NotificationCenterItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Icon { get; set; } = "🔔";
    public string AppTag { get; set; } = "Система";
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string TimeLabel { get; set; } = string.Empty;
    public string BadgeBgHex { get; set; } = "#1A2334";
    public string? ActionLabel { get; set; }
    public string? TargetPath { get; set; }
    public Action? OnAction { get; set; }
    public bool HasAction => !string.IsNullOrEmpty(ActionLabel) && (OnAction != null || !string.IsNullOrEmpty(TargetPath));
    public bool HasFolderButton => !string.IsNullOrEmpty(TargetPath) && System.IO.File.Exists(TargetPath);
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
    public double FeelsLikeC { get; set; }
    public double WindKmh { get; set; }
    public int HumidityPercent { get; set; }
    public int WmoCode { get; set; }
    public string ConditionText { get; set; } = "Ясно";
    public string IconGlyph { get; set; } = "☀";
    public string ShortText => $"{Math.Round(TempC):+0;-0;0}°";
    public string DetailsText => $"Ощущ. {Math.Round(FeelsLikeC):+0;-0;0}° · 💨 {Math.Round(WindKmh)} км/ч · 💧 {HumidityPercent}%";
    public string? RainAlertText { get; set; }
    public List<WeatherHourItem> Hourly { get; set; } = new();
    public List<WeatherDayItem> Daily { get; set; } = new();
}

public class WeatherHourItem
{
    public string TimeLabel { get; set; } = string.Empty;
    public double TempC { get; set; }
    public int WmoCode { get; set; }
    public int PrecipProb { get; set; }
    public string IconGlyph { get; set; } = "☀";
    public string TempLabel => $"{Math.Round(TempC):+0;-0;0}°";
    public string PrecipLabel => PrecipProb >= 15 ? $"{PrecipProb}%" : string.Empty;
}

public class WeatherDayItem
{
    public string DayLabel { get; set; } = string.Empty;
    public int WmoCode { get; set; }
    public string IconGlyph { get; set; } = "☀";
    public double MinTempC { get; set; }
    public double MaxTempC { get; set; }
    public string MinLabel => $"{Math.Round(MinTempC):+0;-0;0}°";
    public string MaxLabel => $"{Math.Round(MaxTempC):+0;-0;0}°";
    public double BarLeftPx { get; set; }
    public double BarWidthPx { get; set; }
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
