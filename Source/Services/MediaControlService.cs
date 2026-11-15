using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopIsland.Models;
using Windows.Media.Control;
using MediaColor = System.Windows.Media.Color;

namespace DesktopIsland.Services;

public static class MediaControlService
{
    private static string _cachedThumbKey = string.Empty;
    private static ImageSource? _cachedThumb;
    private static MediaColor? _cachedDominantColor;
    private const byte VkMediaNextTrack = 0xB0;
    private const byte VkMediaPrevTrack = 0xB1;
    private const byte VkMediaPlayPause = 0xB3;
    private const uint KeyEventFKeyUp = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private static GlobalSystemMediaTransportControlsSessionManager? _manager;

    private static async Task<GlobalSystemMediaTransportControlsSessionManager?> GetManagerAsync()
    {
        if (_manager != null) return _manager;
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch { }
        return _manager;
    }

    private sealed class EvaluatedSession
    {
        public required GlobalSystemMediaTransportControlsSession Session { get; init; }
        public required MediaTrackInfo Info { get; init; }
        public int Score { get; init; }
    }

    private static async Task<EvaluatedSession?> ResolveBestSessionAsync(string preferredPlayer)
    {
        var mgr = await GetManagerAsync();
        if (mgr == null) return null;

        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
        try
        {
            sessions = mgr.GetSessions();
        }
        catch
        {
            sessions = Array.Empty<GlobalSystemMediaTransportControlsSession>();
        }

        var current = mgr.GetCurrentSession();
        if (sessions.Count == 0 && current != null)
            sessions = new[] { current };

        if (sessions.Count == 0) return null;

        var evaluated = new List<EvaluatedSession>();
        foreach (var s in sessions)
        {
            try
            {
                var pb = s.GetPlaybackInfo();
                bool isPlaying = pb?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var props = await s.TryGetMediaPropertiesAsync();
                var src = s.SourceAppUserModelId ?? string.Empty;
                var title = props?.Title?.Trim() ?? string.Empty;
                var artist = props?.Artist?.Trim() ?? string.Empty;
                var album = props?.AlbumTitle?.Trim() ?? string.Empty;

                bool hasTimeline = false;
                double ratio = 0;
                double posSec = 0;
                double durSec = 0;
                try
                {
                    var tl = s.GetTimelineProperties();
                    if (tl != null && tl.EndTime > tl.StartTime)
                    {
                        var total = (tl.EndTime - tl.StartTime).TotalSeconds;
                        var pos = (tl.Position - tl.StartTime).TotalSeconds;
                        if (total > 1)
                        {
                            hasTimeline = true;
                            durSec = total;
                            posSec = Math.Clamp(pos, 0.0, total);
                            ratio = Math.Clamp(posSec / total, 0.0, 1.0);
                        }
                    }
                }
                catch { }

                bool isSpotify = src.Contains("Spotify", StringComparison.OrdinalIgnoreCase);
                bool isYouTubeMusic = src.Contains("YouTubeMusic", StringComparison.OrdinalIgnoreCase)
                                   || title.Contains("YouTube Music", StringComparison.OrdinalIgnoreCase)
                                   || album.Contains("YouTube Music", StringComparison.OrdinalIgnoreCase);
                bool isSoundCloud = src.Contains("SoundCloud", StringComparison.OrdinalIgnoreCase)
                                 || title.Contains("SoundCloud", StringComparison.OrdinalIgnoreCase)
                                 || artist.Contains("SoundCloud", StringComparison.OrdinalIgnoreCase)
                                 || album.Contains("SoundCloud", StringComparison.OrdinalIgnoreCase);
                bool isBrowser = src.Contains("chrome", StringComparison.OrdinalIgnoreCase)
                              || src.Contains("msedge", StringComparison.OrdinalIgnoreCase)
                              || src.Contains("firefox", StringComparison.OrdinalIgnoreCase)
                              || src.Contains("opera", StringComparison.OrdinalIgnoreCase)
                              || src.Contains("brave", StringComparison.OrdinalIgnoreCase)
                              || src.Contains("vivaldi", StringComparison.OrdinalIgnoreCase);
                bool isDedicatedMusic = isSpotify
                                     || isSoundCloud
                                     || isYouTubeMusic
                                     || src.Contains("Yandex", StringComparison.OrdinalIgnoreCase)
                                     || src.Contains("Music", StringComparison.OrdinalIgnoreCase)
                                     || src.Contains("AIMP", StringComparison.OrdinalIgnoreCase)
                                     || src.Contains("foobar", StringComparison.OrdinalIgnoreCase)
                                     || src.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase)
                                     || src.Contains("Tidal", StringComparison.OrdinalIgnoreCase)
                                     || src.Contains("Deezer", StringComparison.OrdinalIgnoreCase)
                                     || album.Contains("SoundCloud", StringComparison.OrdinalIgnoreCase);

                int score = 0;
                if (isPlaying) score += 120;
                if (!string.IsNullOrWhiteSpace(title)) score += 40;
                if (current != null && ReferenceEquals(s, current)) score += 15;

                if (string.Equals(preferredPlayer, "Spotify", StringComparison.OrdinalIgnoreCase))
                {
                    if (isSpotify) score += 350;
                }
                else if (string.Equals(preferredPlayer, "SoundCloud", StringComparison.OrdinalIgnoreCase))
                {
                    if (isSoundCloud) score += 350;
                    else if (isBrowser && !string.IsNullOrWhiteSpace(title)) score += 200;
                }
                else if (string.Equals(preferredPlayer, "YouTubeMusic", StringComparison.OrdinalIgnoreCase))
                {
                    if (isYouTubeMusic) score += 350;
                    else if (isBrowser && !string.IsNullOrWhiteSpace(title)) score += 200;
                }
                else if (isDedicatedMusic)
                {
                    score += 80;
                }

                string label = isSpotify ? "Spotify"
                    : isSoundCloud ? "SoundCloud"
                    : isYouTubeMusic ? "YT Music"
                    : ExtractSourceLabel(src);

                ImageSource? thumbImg = null;
                MediaColor? domColor = null;
                string cacheKey = $"{src}|{title}|{artist}";
                if (props?.Thumbnail != null)
                {
                    if (string.Equals(_cachedThumbKey, cacheKey, StringComparison.Ordinal) && _cachedThumb != null)
                    {
                        thumbImg = _cachedThumb;
                        domColor = _cachedDominantColor;
                    }
                    else
                    {
                        var loaded = await TryLoadThumbnailAsync(props.Thumbnail);
                        _cachedThumbKey = cacheKey;
                        _cachedThumb = loaded.Image;
                        _cachedDominantColor = loaded.Color;
                        thumbImg = loaded.Image;
                        domColor = loaded.Color;
                    }
                }

                evaluated.Add(new EvaluatedSession
                {
                    Session = s,
                    Score = score,
                    Info = new MediaTrackInfo
                    {
                        HasSession = true,
                        IsPlaying = isPlaying,
                        Title = string.IsNullOrWhiteSpace(title) ? label : title,
                        Artist = artist,
                        SourceApp = src,
                        SourceLabel = label,
                        IsDedicatedMusicSource = isDedicatedMusic,
                        HasTimeline = hasTimeline,
                        ProgressRatio = ratio,
                        PositionSeconds = posSec,
                        DurationSeconds = durSec,
                        Thumbnail = thumbImg,
                        DominantColor = domColor
                    }
                });
            }
            catch { }
        }

        return evaluated
            .OrderByDescending(e => e.Score)
            .FirstOrDefault();
    }

    private static string ExtractSourceLabel(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return "Плеер";
        if (src.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (src.Contains("msedge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (src.Contains("opera", StringComparison.OrdinalIgnoreCase)) return "Opera";
        if (src.Contains("firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (src.Contains("brave", StringComparison.OrdinalIgnoreCase)) return "Brave";
        if (src.Contains("Yandex", StringComparison.OrdinalIgnoreCase)) return "Яндекс Музыка";
        var dot = src.LastIndexOf('.');
        var clean = dot >= 0 && dot + 1 < src.Length ? src[(dot + 1)..] : src;
        var bang = clean.IndexOf('!');
        if (bang > 0) clean = clean[..bang];
        return clean.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(ImageSource? Image, MediaColor? Color)> TryLoadThumbnailAsync(Windows.Storage.Streams.IRandomAccessStreamReference thumbRef)
    {
        try
        {
            using var stream = await thumbRef.OpenReadAsync();
            using var netStream = stream.AsStreamForRead();
            using var ms = new MemoryStream();
            await netStream.CopyToAsync(ms);
            ms.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 160;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();

            var color = ExtractDominantColor(bmp);
            return (bmp, color);
        }
        catch
        {
            return (null, null);
        }
    }

    private static MediaColor? ExtractDominantColor(BitmapSource bmp)
    {
        try
        {
            int w = Math.Min(bmp.PixelWidth, 64);
            int h = Math.Min(bmp.PixelHeight, 64);
            if (w <= 0 || h <= 0) return null;

            var formatted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int stride = w * 4;
            var pixels = new byte[h * stride];
            formatted.CopyPixels(new System.Windows.Int32Rect(0, 0, w, h), pixels, stride, 0);

            long sumR = 0, sumG = 0, sumB = 0, weightSum = 0;
            for (int y = 0; y < h; y += 2)
            {
                for (int x = 0; x < w; x += 2)
                {
                    int idx = y * stride + x * 4;
                    byte b = pixels[idx], g = pixels[idx + 1], r = pixels[idx + 2], a = pixels[idx + 3];
                    if (a < 128) continue;
                    int max = Math.Max(r, Math.Max(g, b));
                    int min = Math.Min(r, Math.Min(g, b));
                    if (max < 45) continue; // skip near-black
                    int sat = max - min;
                    int weight = 1 + sat / 16;
                    sumR += r * weight;
                    sumG += g * weight;
                    sumB += b * weight;
                    weightSum += weight;
                }
            }

            if (weightSum <= 0) return null;
            byte ar = (byte)Math.Clamp(sumR / weightSum, 55, 255);
            byte ag = (byte)Math.Clamp(sumG / weightSum, 55, 255);
            byte ab = (byte)Math.Clamp(sumB / weightSum, 55, 255);
            return MediaColor.FromRgb(ar, ag, ab);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<MediaTrackInfo> GetCurrentTrackAsync(string preferredPlayer = "Auto")
    {
        try
        {
            var best = await ResolveBestSessionAsync(preferredPlayer);
            if (best != null)
                return best.Info;
        }
        catch { }
        return new MediaTrackInfo { HasSession = false };
    }

    public static async Task<bool> SeekRatioAsync(double ratio, string preferredPlayer = "Auto")
    {
        try
        {
            var best = await ResolveBestSessionAsync(preferredPlayer);
            if (best == null) return false;
            var tl = best.Session.GetTimelineProperties();
            if (tl == null || tl.EndTime <= tl.StartTime) return false;
            long targetTicks = tl.StartTime.Ticks + (long)((tl.EndTime - tl.StartTime).Ticks * Math.Clamp(ratio, 0.0, 1.0));
            return await best.Session.TryChangePlaybackPositionAsync(targetTicks);
        }
        catch { }
        return false;
    }

    public static async Task PlayPauseAsync(string preferredPlayer = "Auto")
    {
        try
        {
            var best = await ResolveBestSessionAsync(preferredPlayer);
            if (best != null && await best.Session.TryTogglePlayPauseAsync())
                return;
        }
        catch { }
        SendMediaKey(VkMediaPlayPause);
    }

    public static async Task NextAsync(string preferredPlayer = "Auto")
    {
        try
        {
            var best = await ResolveBestSessionAsync(preferredPlayer);
            if (best != null && await best.Session.TrySkipNextAsync())
                return;
        }
        catch { }
        SendMediaKey(VkMediaNextTrack);
    }

    public static async Task PreviousAsync(string preferredPlayer = "Auto")
    {
        try
        {
            var best = await ResolveBestSessionAsync(preferredPlayer);
            if (best != null && await best.Session.TrySkipPreviousAsync())
                return;
        }
        catch { }
        SendMediaKey(VkMediaPrevTrack);
    }

    public static void LaunchOrFocusPlayer(string player)
    {
        try
        {
            if (string.Equals(player, "Spotify", StringComparison.OrdinalIgnoreCase))
            {
                var appDataSpotify = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Spotify\Spotify.exe");
                if (File.Exists(appDataSpotify))
                {
                    Process.Start(new ProcessStartInfo(appDataSpotify) { UseShellExecute = true });
                    return;
                }
                Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true });
            }
            else if (string.Equals(player, "SoundCloud", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo("https://soundcloud.com") { UseShellExecute = true });
            }
            else if (string.Equals(player, "YouTubeMusic", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo("https://music.youtube.com") { UseShellExecute = true });
            }
        }
        catch { }
    }

    private static void SendMediaKey(byte vk)
    {
        try
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, KeyEventFKeyUp, UIntPtr.Zero);
        }
        catch { }
    }
}
