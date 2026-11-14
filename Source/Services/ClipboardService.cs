using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DesktopIsland.Models;

namespace DesktopIsland.Services;

public class ClipboardService : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private HwndSource? _hwndSource;
    private bool _suppressNext;
    private bool _enabled = true;
    private int _maxItems = 20;

    public event Action<string>? ClipboardTextCopied;
    public ObservableCollection<ClipboardEntry> Entries { get; } = new();

    public void Attach(Window window, bool enabled, int maxItems, IEnumerable<ClipboardEntry>? pinned = null)
    {
        _enabled = enabled;
        _maxItems = Math.Clamp(maxItems, 5, 100);

        if (pinned != null)
        {
            foreach (var item in pinned.Where(p => !string.IsNullOrWhiteSpace(p.Text)))
            {
                if (!Entries.Any(e => string.Equals(e.Text, item.Text, StringComparison.Ordinal)))
                {
                    Entries.Add(new ClipboardEntry
                    {
                        Text = item.Text,
                        Timestamp = item.Timestamp,
                        IsPinned = true
                    });
                }
            }
        }

        var helper = new WindowInteropHelper(window);
        if (helper.Handle == IntPtr.Zero) return;

        _hwndSource = HwndSource.FromHwnd(helper.Handle);
        _hwndSource?.AddHook(WndProc);
        AddClipboardFormatListener(helper.Handle);
    }

    public void UpdateConfig(bool enabled, int maxItems)
    {
        _enabled = enabled;
        _maxItems = Math.Clamp(maxItems, 5, 100);
        TrimExcess();
    }

    public List<ClipboardEntry> GetPinnedEntries()
    {
        return Entries
            .Where(e => e.IsPinned)
            .Select(e => new ClipboardEntry { Text = e.Text, Timestamp = e.Timestamp, IsPinned = true })
            .ToList();
    }

    public void TogglePin(ClipboardEntry entry)
    {
        entry.IsPinned = !entry.IsPinned;
        SortPinnedFirst();
    }

    public void ClearUnpinned()
    {
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            if (!Entries[i].IsPinned)
                Entries.RemoveAt(i);
        }
    }

    public void Clear() => ClearUnpinned();

    private void SortPinnedFirst()
    {
        var pinned = Entries.Where(e => e.IsPinned).ToList();
        var unpinned = Entries.Where(e => !e.IsPinned).ToList();
        Entries.Clear();
        foreach (var p in pinned) Entries.Add(p);
        foreach (var u in unpinned) Entries.Add(u);
    }

    private void TrimExcess()
    {
        while (Entries.Count(e => !e.IsPinned) > _maxItems)
        {
            var lastUnpinned = Entries.LastOrDefault(e => !e.IsPinned);
            if (lastUnpinned == null) break;
            Entries.Remove(lastUnpinned);
        }
    }

    public void CopyEntry(string text)
    {
        try
        {
            _suppressNext = true;
            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            _suppressNext = false;
            LoggerService.Error("Failed to write to clipboard.", ex);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            OnClipboardChanged();
        }
        return IntPtr.Zero;
    }

    private void OnClipboardChanged()
    {
        if (!_enabled) return;
        if (_suppressNext)
        {
            _suppressNext = false;
            return;
        }

        try
        {
            // Avoid touching sensitive/password-flagged clipboard formats when present
            var dataObj = System.Windows.Clipboard.GetDataObject();
            if (dataObj == null) return;
            if (dataObj.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return;
            if (!System.Windows.Clipboard.ContainsText()) return;

            var text = System.Windows.Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || text.Length > 65536) return;

            // Remove existing duplicate if already in list (unless pinned, keep pinned state)
            bool wasPinned = false;
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                if (string.Equals(Entries[i].Text, text, StringComparison.Ordinal))
                {
                    wasPinned = Entries[i].IsPinned;
                    Entries.RemoveAt(i);
                }
            }

            if (wasPinned)
            {
                Entries.Insert(0, new ClipboardEntry { Text = text, Timestamp = DateTime.Now, IsPinned = true });
            }
            else
            {
                int insertIndex = Entries.Count(e => e.IsPinned);
                Entries.Insert(insertIndex, new ClipboardEntry { Text = text, Timestamp = DateTime.Now, IsPinned = false });
            }
            TrimExcess();
            ClipboardTextCopied?.Invoke(text);
        }
        catch
        {
            // Clipboard may be temporarily locked by another app; ignore silently
        }
    }

    public void Dispose()
    {
        try
        {
            if (_hwndSource != null)
            {
                RemoveClipboardFormatListener(_hwndSource.Handle);
                _hwndSource.RemoveHook(WndProc);
                _hwndSource = null;
            }
        }
        catch
        {
            // Ignore cleanup failure on shutdown
        }
    }
}
