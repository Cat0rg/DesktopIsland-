using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DesktopIsland.Services;

public record LyricLine(double TimeSeconds, string Text);

public sealed class TrackLyricsResult
{
    public bool Found { get; set; }
    public bool IsSynced { get; set; }
    public List<LyricLine> Lines { get; set; } = new();
    public string RawPlainText { get; set; } = string.Empty;
}

public static class LyricsService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly Regex LrcTagRegex = new(@"\[(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?\]", RegexOptions.Compiled);

    private static string _cachedKey = string.Empty;
    private static TrackLyricsResult? _cachedResult;

    static LyricsService()
    {
        try { Http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopIsland/1.6 (https://github.com/)"); } catch { }
    }

    public static async Task<TrackLyricsResult> GetLyricsAsync(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new TrackLyricsResult { Found = false };

        string cleanTitle = CleanTitle(title);
        string cleanArtist = (artist ?? string.Empty).Trim();
        string cacheKey = $"{cleanArtist}|{cleanTitle}".ToLowerInvariant();
        if (_cachedResult != null && string.Equals(_cachedKey, cacheKey, StringComparison.Ordinal))
            return _cachedResult;

        TrackLyricsResult result = new() { Found = false };
        try
        {
            string url = !string.IsNullOrWhiteSpace(cleanArtist)
                ? $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(cleanArtist)}&track_name={Uri.EscapeDataString(cleanTitle)}"
                : $"https://lrclib.net/api/search?q={Uri.EscapeDataString(cleanTitle)}";

            using var resp = await Http.GetAsync(url);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                result = ParseLrcLibPayload(json);
            }

            if (!result.Found && !string.IsNullOrWhiteSpace(cleanArtist))
            {
                string searchUrl = $"https://lrclib.net/api/search?q={Uri.EscapeDataString($"{cleanArtist} {cleanTitle}")}";
                using var resp2 = await Http.GetAsync(searchUrl);
                if (resp2.IsSuccessStatusCode)
                {
                    var json2 = await resp2.Content.ReadAsStringAsync();
                    result = ParseLrcLibPayload(json2);
                }
            }
        }
        catch
        {
            result = new TrackLyricsResult { Found = false };
        }

        _cachedKey = cacheKey;
        _cachedResult = result;
        return result;
    }

    private static string CleanTitle(string raw)
    {
        var t = Regex.Replace(raw, @"\s*[\(\[]\s*(?:official|lyrics|audio|video|hd|4k|feat\.?.*?|ft\.?.*?)\s*[\)\]]", string.Empty, RegexOptions.IgnoreCase);
        return t.Trim();
    }

    private static TrackLyricsResult ParseLrcLibPayload(string json)
    {
        using var doc = JsonDocument.Parse(json);
        JsonElement item = doc.RootElement;
        if (item.ValueKind == JsonValueKind.Array)
        {
            if (item.GetArrayLength() == 0)
                return new TrackLyricsResult { Found = false };
            JsonElement? syncedItem = null;
            foreach (var el in item.EnumerateArray())
            {
                if (el.TryGetProperty("syncedLyrics", out var sl) && sl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(sl.GetString()))
                {
                    syncedItem = el;
                    break;
                }
            }
            item = syncedItem ?? item[0];
        }

        if (item.ValueKind != JsonValueKind.Object)
            return new TrackLyricsResult { Found = false };

        string? synced = item.TryGetProperty("syncedLyrics", out var sProp) && sProp.ValueKind == JsonValueKind.String
            ? sProp.GetString() : null;
        string? plain = item.TryGetProperty("plainLyrics", out var pProp) && pProp.ValueKind == JsonValueKind.String
            ? pProp.GetString() : null;

        if (!string.IsNullOrWhiteSpace(synced))
        {
            var lines = ParseLrc(synced);
            if (lines.Count > 0)
                return new TrackLyricsResult { Found = true, IsSynced = true, Lines = lines, RawPlainText = plain ?? string.Empty };
        }

        if (!string.IsNullOrWhiteSpace(plain))
        {
            var plainLines = new List<LyricLine>();
            foreach (var l in plain.Split('\n'))
            {
                var trimmed = l.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    plainLines.Add(new LyricLine(-1, trimmed));
            }
            if (plainLines.Count > 0)
                return new TrackLyricsResult { Found = true, IsSynced = false, Lines = plainLines, RawPlainText = plain };
        }

        return new TrackLyricsResult { Found = false };
    }

    private static List<LyricLine> ParseLrc(string lrc)
    {
        var list = new List<LyricLine>();
        foreach (var rawLine in lrc.Split('\n'))
        {
            var matches = LrcTagRegex.Matches(rawLine);
            if (matches.Count == 0) continue;
            string text = LrcTagRegex.Replace(rawLine, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            foreach (Match m in matches)
            {
                int mins = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                int secs = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                double frac = 0;
                if (m.Groups[3].Success)
                {
                    string f = m.Groups[3].Value;
                    frac = int.Parse(f, CultureInfo.InvariantCulture) / Math.Pow(10, f.Length);
                }
                list.Add(new LyricLine(mins * 60.0 + secs + frac, text));
            }
        }
        list.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
        return list;
    }
}
