using System;
using System.Threading.Tasks;
using DesktopIsland.Models;
using Windows.Media.Control;

namespace DesktopIsland.Services;

public static class MediaControlService
{
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

    public static async Task<MediaTrackInfo> GetCurrentTrackAsync()
    {
        try
        {
            var mgr = await GetManagerAsync();
            var session = mgr?.GetCurrentSession();
            if (session == null)
                return new MediaTrackInfo { HasSession = false };

            var pb = session.GetPlaybackInfo();
            bool isPlaying = pb?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var props = await session.TryGetMediaPropertiesAsync();

            var title = string.IsNullOrWhiteSpace(props?.Title) ? "Медиа" : props.Title.Trim();
            var artist = string.IsNullOrWhiteSpace(props?.Artist) ? string.Empty : props.Artist.Trim();
            var src = session.SourceAppUserModelId ?? string.Empty;

            return new MediaTrackInfo
            {
                HasSession = true,
                IsPlaying = isPlaying,
                Title = title,
                Artist = artist,
                SourceApp = src
            };
        }
        catch
        {
            return new MediaTrackInfo { HasSession = false };
        }
    }

    public static async Task PlayPauseAsync()
    {
        try
        {
            var session = (await GetManagerAsync())?.GetCurrentSession();
            if (session != null) await session.TryTogglePlayPauseAsync();
        }
        catch { }
    }

    public static async Task NextAsync()
    {
        try
        {
            var session = (await GetManagerAsync())?.GetCurrentSession();
            if (session != null) await session.TrySkipNextAsync();
        }
        catch { }
    }

    public static async Task PreviousAsync()
    {
        try
        {
            var session = (await GetManagerAsync())?.GetCurrentSession();
            if (session != null) await session.TrySkipPreviousAsync();
        }
        catch { }
    }
}
