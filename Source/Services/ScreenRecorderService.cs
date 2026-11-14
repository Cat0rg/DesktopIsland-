using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DesktopIsland.Services;

/// <summary>
/// Self-contained screen recorder powered by Windows Media Foundation (H.264 MP4) and GDI capture.
/// </summary>
public sealed class ScreenRecorderService : IDisposable
{
    public static string RecordingsDirectory
    {
        get
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrWhiteSpace(videos))
                videos = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            return Path.Combine(videos, "DesktopIsland");
        }
    }

    public static string GetReplayClipsDirectory()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        try
        {
            bool hasNvidia = Process.GetProcessesByName("nvcontainer").Length > 0
                          || Process.GetProcessesByName("NVIDIA Overlay").Length > 0;
            if (hasNvidia)
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\NVIDIA Corporation\Global\ShadowPlay\NVSPCAPS");
                if (key?.GetValue("DefaultPathW") is byte[] bytes && bytes.Length >= 2)
                {
                    var path = System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0', ' ');
                    if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                        return path;
                }
                else if (key?.GetValue("DefaultPathW") is string str && !string.IsNullOrWhiteSpace(str) && Directory.Exists(str))
                {
                    return str;
                }

                var nvVideos = Path.Combine(videos, "NVIDIA");
                if (Directory.Exists(nvVideos)) return nvVideos;
            }
        }
        catch { }

        var captures = Path.Combine(videos, "Captures");
        return Directory.Exists(captures) ? captures : videos;
    }

    public static string ShortFolderLabel(string fullPath)
    {
        try
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (!string.IsNullOrEmpty(videos) && fullPath.StartsWith(videos, StringComparison.OrdinalIgnoreCase))
            {
                var rel = fullPath[videos.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.IsNullOrEmpty(rel) ? "Видео" : $"Видео\\{rel}";
            }
            var parts = fullPath.TrimEnd(Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar);
            return parts.Length >= 2 ? $"{parts[^2]}\\{parts[^1]}" : fullPath;
        }
        catch { return fullPath; }
    }

    private Thread? _captureThread;
    private volatile bool _stopRequested;
    private readonly Stopwatch _sw = new();

    public bool IsRecording { get; private set; }
    public TimeSpan Elapsed => IsRecording ? _sw.Elapsed : TimeSpan.Zero;
    public string? CurrentOutputPath { get; private set; }
    public string? LastError { get; private set; }
    public int CapturedFrames { get; private set; }
    public event Action<string>? RecordingError;

    public bool Start(int monitorIndex, out string? errorMessage)
    {
        errorMessage = null;
        if (IsRecording) return true;

        var screens = Forms.Screen.AllScreens;
        if (screens.Length == 0)
        {
            errorMessage = "Мониторы не найдены";
            return false;
        }

        int idx = monitorIndex >= 0 && monitorIndex < screens.Length
            ? monitorIndex
            : Array.IndexOf(screens, Forms.Screen.PrimaryScreen);
        if (idx < 0) idx = 0;

        var bounds = screens[idx].Bounds;
        int width = bounds.Width & ~1;
        int height = bounds.Height & ~1;
        if (width < 64 || height < 64)
        {
            errorMessage = "Некорректное разрешение экрана";
            return false;
        }

        try
        {
            Directory.CreateDirectory(RecordingsDirectory);
            string filePath = Path.Combine(RecordingsDirectory, $"Screen_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");

            _stopRequested = false;
            LastError = null;
            CapturedFrames = 0;
            CurrentOutputPath = filePath;
            IsRecording = true;
            _sw.Restart();

            var ready = new ManualResetEventSlim(false);
            string? initErr = null;

            _captureThread = new Thread(() => CaptureLoop(bounds, width, height, filePath, ready, err => initErr = err))
            {
                IsBackground = true,
                Name = "DesktopIsland.ScreenRecorder"
            };
            _captureThread.SetApartmentState(ApartmentState.MTA);
            _captureThread.Start();

            ready.Wait(3000);
            if (initErr != null)
            {
                IsRecording = false;
                _sw.Stop();
                CurrentOutputPath = null;
                errorMessage = initErr;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            IsRecording = false;
            _sw.Stop();
            CurrentOutputPath = null;
            errorMessage = ex.Message;
            return false;
        }
    }

    public string? Stop()
    {
        if (!IsRecording) return null;
        _stopRequested = true;
        try { _captureThread?.Join(4500); } catch { }
        _captureThread = null;
        _sw.Stop();
        IsRecording = false;
        var saved = CurrentOutputPath;
        CurrentOutputPath = null;
        return saved != null && File.Exists(saved) ? saved : null;
    }

    public void Dispose()
    {
        if (IsRecording) Stop();
    }

    private void CaptureLoop(
        Rectangle bounds,
        int width,
        int height,
        string outputPath,
        ManualResetEventSlim ready,
        Action<string> onInitError)
    {
        bool mfStarted = false;
        IMFSinkWriter? writer = null;
        IMFAttributes? writerAttrs = null;
        IMFAttributes? outType = null;
        IMFAttributes? inType = null;
        bool signaled = false;

        try
        {
            int hr = MFStartup(MF_VERSION, 0);
            if (hr < 0)
            {
                onInitError($"MFStartup failed (0x{hr:X8})");
                ready.Set();
                return;
            }
            mfStarted = true;

            const int fps = 30;
            uint bitrate = width >= 2500 ? 12_000_000u : 8_000_000u;

            // Keep CPU-side SinkWriter reliably on inbox Color Converter + H.264 encoder (avoid hw MFT without D3D manager)
            hr = MFCreateSinkWriterFromURL(outputPath, IntPtr.Zero, null, out writer);
            if (hr < 0 || writer == null)
            {
                onInitError($"Не удалось создать MP4 (0x{hr:X8})");
                ready.Set();
                return;
            }

            // Output type: H.264
            hr = MFCreateMediaType(out outType);
            if (hr < 0 || outType == null) { onInitError($"MFCreateMediaType(out) failed (0x{hr:X8})"); ready.Set(); return; }
            outType.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            outType.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
            outType.SetUINT32(MF_MT_AVG_BITRATE, bitrate);
            outType.SetUINT32(MF_MT_INTERLACE_MODE, 2); // Progressive
            outType.SetUINT64(MF_MT_FRAME_SIZE, Pack2((uint)width, (uint)height));
            outType.SetUINT64(MF_MT_FRAME_RATE, Pack2(fps, 1));
            outType.SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, Pack2(1, 1));

            hr = writer.AddStream(outType, out uint streamIndex);
            if (hr < 0)
            {
                onInitError($"H.264 кодек недоступен ({width}x{height}, 0x{hr:X8})");
                ready.Set();
                return;
            }

            // Input type: RGB32 top-down
            hr = MFCreateMediaType(out inType);
            if (hr < 0 || inType == null) { onInitError($"MFCreateMediaType(in) failed (0x{hr:X8})"); ready.Set(); return; }
            inType.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            inType.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
            inType.SetUINT32(MF_MT_INTERLACE_MODE, 2);
            inType.SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            inType.SetUINT32(MF_MT_DEFAULT_STRIDE, (uint)(width * 4));
            inType.SetUINT64(MF_MT_FRAME_SIZE, Pack2((uint)width, (uint)height));
            inType.SetUINT64(MF_MT_FRAME_RATE, Pack2(fps, 1));
            inType.SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, Pack2(1, 1));

            hr = writer.SetInputMediaType(streamIndex, inType, IntPtr.Zero);
            if (hr < 0)
            {
                onInitError($"SetInputMediaType failed ({width}x{height}, 0x{hr:X8})");
                ready.Set();
                return;
            }

            hr = writer.BeginWriting();
            if (hr < 0)
            {
                onInitError($"BeginWriting failed (0x{hr:X8})");
                ready.Set();
                return;
            }

            ready.Set();
            signaled = true;

            int bufferSize = width * height * 4;
            const long ticksPerFrame = 10_000_000L / fps; // 333,333 hns (~33.3ms)
            long frameIntervalMs = 1000 / fps;
            var clock = Stopwatch.StartNew();
            long frameIndex = 0;

            IntPtr memDc = IntPtr.Zero;
            IntPtr hDib = IntPtr.Zero;
            IntPtr oldObj = IntPtr.Zero;
            IntPtr dibBits = IntPtr.Zero;
            try
            {
                memDc = CreateCompatibleDC(IntPtr.Zero);
                var bmi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = width,
                        biHeight = -height, // top-down DIB matching positive MF_MT_DEFAULT_STRIDE
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0 // BI_RGB
                    }
                };
                hDib = CreateDIBSection(memDc, ref bmi, 0, out dibBits, IntPtr.Zero, 0);
                if (memDc == IntPtr.Zero || hDib == IntPtr.Zero || dibBits == IntPtr.Zero)
                {
                    LastError = "Не удалось инициализировать GDI DIB для захвата экрана";
                    RecordingError?.Invoke(LastError);
                    return;
                }
                oldObj = SelectObject(memDc, hDib);

                bool tryCaptureBlt = true;
                while (!_stopRequested)
                {
                    long frameStartMs = clock.ElapsedMilliseconds;
                    long sampleTime = frameStartMs * 10_000L; // ms -> 100ns units

                    IntPtr screenDc = GetDC(IntPtr.Zero);
                    if (screenDc != IntPtr.Zero)
                    {
                        try
                        {
                            const uint SRCCOPY = 0x00CC0020;
                            const uint CAPTUREBLT = 0x40000000;
                            bool bltOk = tryCaptureBlt && BitBlt(memDc, 0, 0, width, height, screenDc, bounds.X, bounds.Y, SRCCOPY | CAPTUREBLT);
                            if (!bltOk)
                            {
                                bltOk = BitBlt(memDc, 0, 0, width, height, screenDc, bounds.X, bounds.Y, SRCCOPY);
                                if (bltOk) tryCaptureBlt = false;
                            }
                            if (bltOk)
                            {
                                CapturedFrames++;
                                DrawMouseCursor(memDc, bounds.X, bounds.Y);
                            }
                        }
                        finally
                        {
                            ReleaseDC(IntPtr.Zero, screenDc);
                        }
                    }

                    int bufHr = MFCreateMemoryBuffer((uint)bufferSize, out var mediaBuffer);
                    if (bufHr < 0 || mediaBuffer == null)
                    {
                        LastError ??= $"MFCreateMemoryBuffer failed (0x{bufHr:X8})";
                    }
                    else
                    {
                        try
                        {
                            int lockHr = mediaBuffer.Lock(out IntPtr dstPtr, out _, out _);
                            if (lockHr < 0)
                            {
                                LastError = $"mediaBuffer.Lock failed (0x{lockHr:X8})";
                                break;
                            }
                            try
                            {
                                CopyMemory(dstPtr, dibBits, (UIntPtr)bufferSize);
                            }
                            finally
                            {
                                mediaBuffer.Unlock();
                            }

                            int lenHr = mediaBuffer.SetCurrentLength((uint)bufferSize);
                            if (lenHr < 0)
                            {
                                LastError = $"SetCurrentLength failed (0x{lenHr:X8})";
                                break;
                            }

                            int sampHr = MFCreateSample(out var sample);
                            if (sampHr < 0 || sample == null)
                            {
                                LastError ??= $"MFCreateSample failed (0x{sampHr:X8})";
                            }
                            else
                            {
                                try
                                {
                                    int addHr = sample.AddBuffer(mediaBuffer);
                                    int timeHr = sample.SetSampleTime(sampleTime);
                                    int durHr = sample.SetSampleDuration(ticksPerFrame);
                                    if (addHr < 0 || timeHr < 0 || durHr < 0)
                                    {
                                        LastError = $"Sample prep failed (add=0x{addHr:X8}, time=0x{timeHr:X8}, dur=0x{durHr:X8})";
                                        break;
                                    }
                                    int wHr = writer.WriteSample(streamIndex, sample);
                                    if (wHr < 0)
                                    {
                                        LastError = $"WriteSample failed (0x{wHr:X8})";
                                        RecordingError?.Invoke(LastError);
                                        break;
                                    }
                                }
                                finally
                                {
                                    Marshal.ReleaseComObject(sample);
                                }
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(mediaBuffer);
                        }
                    }

                    frameIndex++;
                    long targetNextMs = frameIndex * frameIntervalMs;
                    long sleepMs = targetNextMs - clock.ElapsedMilliseconds;
                    if (sleepMs > 1)
                        Thread.Sleep((int)Math.Min(sleepMs, 40));
                }
            }
            finally
            {
                if (memDc != IntPtr.Zero && oldObj != IntPtr.Zero) SelectObject(memDc, oldObj);
                if (hDib != IntPtr.Zero) DeleteObject(hDib);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
            }

            if (frameIndex == 0 && LastError == null)
            {
                LastError = "0 кадров захвачено";
                RecordingError?.Invoke(LastError);
            }
            int fHr = writer.FinalizeWriting();
            if (fHr < 0)
            {
                LastError ??= $"Finalize failed (0x{fHr:X8}, frames={frameIndex}, {width}x{height})";
                RecordingError?.Invoke(LastError);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.ToString();
            if (!signaled)
            {
                onInitError(ex.Message);
                ready.Set();
            }
            else
            {
                RecordingError?.Invoke(ex.Message);
            }
        }
        finally
        {
            if (inType != null) try { Marshal.ReleaseComObject(inType); } catch { }
            if (outType != null) try { Marshal.ReleaseComObject(outType); } catch { }
            if (writerAttrs != null) try { Marshal.ReleaseComObject(writerAttrs); } catch { }
            if (writer != null) try { Marshal.ReleaseComObject(writer); } catch { }
            if (mfStarted) try { MFShutdown(); } catch { }
        }
    }

    private static void DrawMouseCursor(IntPtr hdc, int screenOffsetX, int screenOffsetY)
    {
        try
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(out ci) || (ci.flags & 0x00000001) == 0 || ci.hCursor == IntPtr.Zero)
                return;

            int hotspotX = 0, hotspotY = 0;
            if (GetIconInfo(ci.hCursor, out var ii))
            {
                hotspotX = ii.xHotspot;
                hotspotY = ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }

            int x = ci.ptScreenPos.x - screenOffsetX - hotspotX;
            int y = ci.ptScreenPos.y - screenOffsetY - hotspotY;
            DrawIconEx(hdc, x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, 0x0003 /* DI_NORMAL */);
        }
        catch { }
    }

    private static ulong Pack2(uint high, uint low) => ((ulong)high << 32) | low;

    private const uint MF_VERSION = 0x00020070;

    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr dest, IntPtr src, UIntPtr length);

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(uint version, uint dwFlags);

    [DllImport("mfplat.dll")]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMediaType(out IMFAttributes ppMFType);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateAttributes(out IMFAttributes ppMFAttributes, uint cInitialSize);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMemoryBuffer(uint cbMaxLength, out IMFMediaBuffer ppBuffer);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateSample(out IMFSample ppSample);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateSinkWriterFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string pwszOutputURL,
        IntPtr pByteStream,
        [MarshalAs(UnmanagedType.Interface)] IMFAttributes? pAttributes,
        out IMFSinkWriter ppSinkWriter);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(out CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyWidth, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
        [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
        [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr Value, out bool pbResult);
        [PreserveSig] int Compare(IMFAttributes pTheirs, int MatchType, out bool pbResult);
        [PreserveSig] int GetUINT32(ref Guid guidKey, out uint punValue);
        [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong punValue);
        [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
        [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
        [PreserveSig] int GetStringLength(ref Guid guidKey, out uint pcchLength);
        [PreserveSig] int GetString(ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        [PreserveSig] int GetBlob(ref Guid guidKey, [Out] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
        [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(ref Guid guidKey, IntPtr Value);
        [PreserveSig] int DeleteItem(ref Guid guidKey);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(in Guid guidKey, uint unValue);
        [PreserveSig] int SetUINT64(in Guid guidKey, ulong unValue);
        [PreserveSig] int SetDouble(ref Guid guidKey, double fValue);
        [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);
        [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        [PreserveSig] int SetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize);
        [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint pcItems);
        [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems(IMFAttributes pDest);
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
        [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
        [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
        [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
        [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr Value, out bool pbResult);
        [PreserveSig] int Compare(IMFAttributes pTheirs, int MatchType, out bool pbResult);
        [PreserveSig] int GetUINT32(ref Guid guidKey, out uint punValue);
        [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong punValue);
        [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
        [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
        [PreserveSig] int GetStringLength(ref Guid guidKey, out uint pcchLength);
        [PreserveSig] int GetString(ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        [PreserveSig] int GetBlob(ref Guid guidKey, [Out] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
        [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(ref Guid guidKey, IntPtr Value);
        [PreserveSig] int DeleteItem(ref Guid guidKey);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(in Guid guidKey, uint unValue);
        [PreserveSig] int SetUINT64(in Guid guidKey, ulong unValue);
        [PreserveSig] int SetDouble(ref Guid guidKey, double fValue);
        [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);
        [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        [PreserveSig] int SetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize);
        [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint pcItems);
        [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems(IMFAttributes pDest);

        [PreserveSig] int GetSampleFlags(out uint pdwSampleFlags);
        [PreserveSig] int SetSampleFlags(uint dwSampleFlags);
        [PreserveSig] int GetSampleTime(out long phnsSampleTime);
        [PreserveSig] int SetSampleTime(long hnsSampleTime);
        [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
        [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
        [PreserveSig] int GetBufferCount(out uint pdwBufferCount);
        [PreserveSig] int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
        [PreserveSig] int RemoveBufferByIndex(uint dwIndex);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out uint pcbTotalLength);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer pBuffer);
    }

    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSinkWriter
    {
        [PreserveSig] int AddStream([MarshalAs(UnmanagedType.Interface)] object pTargetMediaType, out uint pdwStreamIndex);
        [PreserveSig] int SetInputMediaType(uint dwStreamIndex, [MarshalAs(UnmanagedType.Interface)] object pInputMediaType, IntPtr pEncodingParameters);
        [PreserveSig] int BeginWriting();
        [PreserveSig] int WriteSample(uint dwStreamIndex, IMFSample pSample);
        [PreserveSig] int SendStreamTick(uint dwStreamIndex, long llTimestamp);
        [PreserveSig] int PlaceMarker(uint dwStreamIndex, IntPtr pvContext);
        [PreserveSig] int NotifyEndOfSegment(uint dwStreamIndex);
        [PreserveSig] int Flush(uint dwStreamIndex);
        [PreserveSig] int FinalizeWriting();
        [PreserveSig] int GetServiceForStream(uint dwStreamIndex, ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
        [PreserveSig] int GetStatistics(uint dwStreamIndex, IntPtr pStats);
    }
}
