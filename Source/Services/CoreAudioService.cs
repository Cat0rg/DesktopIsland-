using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DesktopIsland.Models;

namespace DesktopIsland.Services;

public static class CoreAudioService
{
    private const int CLSCTX_ALL = 23;
    private const int STGM_READ = 0;
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid IID_IAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid CLSID_PolicyConfigClient = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

    private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr p1;
        public IntPtr p2;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out uint pcDevices);
        int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        int GetState(out int pdwState);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint cProps);
        int GetAt(uint iProp, out PROPERTYKEY pkey);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr pNotify);
        int UnregisterControlChangeNotify(IntPtr pNotify);
        int GetChannelCount(out uint pnChannelCount);
        int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        int GetMasterVolumeLevel(out float pfLevelDB);
        int GetMasterVolumeLevelScalar(out float pfLevel);
        int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        int GetPeakValue(out float pfPeak);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr AudioSessionGuid, uint StreamFlags, out IntPtr SessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr AudioSessionGuid, uint StreamFlags, out IntPtr AudioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int SessionCount);
        [PreserveSig] int GetSession(int SessionIndex, out IAudioSessionControl Session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int pRetVal);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        [PreserveSig] int GetGroupingParam(out Guid pRetVal);
        [PreserveSig] int SetGroupingParam(ref Guid Override, ref Guid EventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr NewNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr NewNotifications);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int pRetVal);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        [PreserveSig] int GetGroupingParam(out Guid pRetVal);
        [PreserveSig] int SetGroupingParam(ref Guid Override, ref Guid EventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr NewNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr NewNotifications);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetProcessId(out uint pRetVal);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float fLevel, ref Guid EventContext);
        [PreserveSig] int GetMasterVolume(out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid EventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
        int GetDeviceFormat(string pszDeviceName, bool bDefault, IntPtr ppFormat);
        int ResetDeviceFormat(string pszDeviceName);
        int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
        int GetProcessingPeriod(string pszDeviceName, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
        int GetShareMode(string pszDeviceName, IntPtr pMode);
        int SetShareMode(string pszDeviceName, IntPtr mode);
        int GetPropertyValue(string pszDeviceName, bool bFxStore, ref PROPERTYKEY key, out PROPVARIANT pv);
        int SetPropertyValue(string pszDeviceName, bool bFxStore, ref PROPERTYKEY key, ref PROPVARIANT pv);
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);
    }

    private static int _renderMeterVersion;
    [ThreadStatic] private static IAudioMeterInformation? t_renderMeter;
    [ThreadStatic] private static int t_renderMeterVersion;
    [ThreadStatic] private static long t_renderMeterAcquiredMs;

    private static void ReleaseCom(object? obj)
    {
        if (obj != null && Marshal.IsComObject(obj))
        {
            try { Marshal.ReleaseComObject(obj); } catch { }
        }
    }

    public static void InvalidateRenderMeterCache() => System.Threading.Interlocked.Increment(ref _renderMeterVersion);

    private static IMMDeviceEnumerator? CreateEnumerator()
    {
        var type = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
        return type is null ? null : Activator.CreateInstance(type) as IMMDeviceEnumerator;
    }

    public static int GetMasterVolumePercent()
    {
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        object? obj = null;
        try
        {
            en = CreateEnumerator();
            if (en == null) return 0;
            if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out dev) != 0 || dev == null) return 0;
            var iid = IID_IAudioEndpointVolume;
            dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out obj);
            if (obj is IAudioEndpointVolume vol)
            {
                vol.GetMasterVolumeLevelScalar(out float level);
                return Math.Clamp((int)Math.Round(level * 100f), 0, 100);
            }
        }
        catch { }
        finally
        {
            ReleaseCom(obj);
            ReleaseCom(dev);
            ReleaseCom(en);
        }
        return 0;
    }

    public static int ChangeMasterVolume(int deltaPercent)
    {
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        object? obj = null;
        try
        {
            en = CreateEnumerator();
            if (en == null) return 0;
            if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out dev) != 0 || dev == null) return 0;
            var iid = IID_IAudioEndpointVolume;
            dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out obj);
            if (obj is IAudioEndpointVolume vol)
            {
                vol.GetMasterVolumeLevelScalar(out float cur);
                float next = Math.Clamp(cur + deltaPercent / 100f, 0f, 1f);
                var ctx = Guid.Empty;
                vol.SetMasterVolumeLevelScalar(next, ref ctx);
                if (next > 0) vol.SetMute(false, ref ctx);
                return Math.Clamp((int)Math.Round(next * 100f), 0, 100);
            }
        }
        catch { }
        finally
        {
            ReleaseCom(obj);
            ReleaseCom(dev);
            ReleaseCom(en);
        }
        return GetMasterVolumePercent();
    }

    public static void SetMasterVolumePercent(int percent)
    {
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        object? obj = null;
        try
        {
            en = CreateEnumerator();
            if (en == null) return;
            if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out dev) != 0 || dev == null) return;
            var iid = IID_IAudioEndpointVolume;
            dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out obj);
            if (obj is IAudioEndpointVolume vol)
            {
                float next = Math.Clamp(percent / 100f, 0f, 1f);
                var ctx = Guid.Empty;
                vol.SetMasterVolumeLevelScalar(next, ref ctx);
                if (next > 0) vol.SetMute(false, ref ctx);
            }
        }
        catch { }
        finally
        {
            ReleaseCom(obj);
            ReleaseCom(dev);
            ReleaseCom(en);
        }
    }

    public static float GetRenderPeakLevel()
    {
        try
        {
            int curVersion = System.Threading.Volatile.Read(ref _renderMeterVersion);
            long now = Environment.TickCount64;
            if (t_renderMeter == null || t_renderMeterVersion != curVersion || now - t_renderMeterAcquiredMs >= 3000)
            {
                ReleaseCom(t_renderMeter);
                t_renderMeter = null;

                IMMDeviceEnumerator? en = null;
                IMMDevice? dev = null;
                try
                {
                    en = CreateEnumerator();
                    if (en != null && en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out dev) == 0 && dev != null)
                    {
                        var iid = IID_IAudioMeterInformation;
                        dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj);
                        if (obj is IAudioMeterInformation meter)
                        {
                            t_renderMeter = meter;
                            t_renderMeterVersion = curVersion;
                            t_renderMeterAcquiredMs = now;
                        }
                        else
                        {
                            ReleaseCom(obj);
                        }
                    }
                }
                finally
                {
                    ReleaseCom(dev);
                    ReleaseCom(en);
                }
            }

            if (t_renderMeter != null && t_renderMeter.GetPeakValue(out float peak) == 0)
                return Math.Clamp(peak, 0f, 1f);
        }
        catch
        {
            ReleaseCom(t_renderMeter);
            t_renderMeter = null;
        }
        return 0f;
    }

    public static bool IsMicMuted()
    {
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        object? obj = null;
        try
        {
            en = CreateEnumerator();
            if (en == null) return false;
            if (en.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eCommunications, out dev) != 0 || dev == null)
            {
                ReleaseCom(dev);
                dev = null;
                if (en.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out dev) != 0 || dev == null) return false;
            }
            var iid = IID_IAudioEndpointVolume;
            dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out obj);
            if (obj is IAudioEndpointVolume vol)
            {
                vol.GetMute(out bool muted);
                return muted;
            }
        }
        catch { }
        finally
        {
            ReleaseCom(obj);
            ReleaseCom(dev);
            ReleaseCom(en);
        }
        return false;
    }

    public static bool ToggleMicMute()
    {
        bool next = !IsMicMuted();
        SetMicMute(next);
        return next;
    }

    public static bool SetMicMute(bool mute)
    {
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        object? obj = null;
        try
        {
            en = CreateEnumerator();
            if (en == null) return false;
            if (en.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eCommunications, out dev) != 0 || dev == null)
            {
                ReleaseCom(dev);
                dev = null;
                if (en.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out dev) != 0 || dev == null) return false;
            }
            var iid = IID_IAudioEndpointVolume;
            dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out obj);
            if (obj is IAudioEndpointVolume vol)
            {
                var ctx = Guid.Empty;
                vol.SetMute(mute, ref ctx);
                return true;
            }
        }
        catch { }
        finally
        {
            ReleaseCom(obj);
            ReleaseCom(dev);
            ReleaseCom(en);
        }
        return false;
    }

    public static List<AudioDeviceItem> GetPlaybackDevices()
    {
        var list = new List<AudioDeviceItem>();
        IMMDeviceEnumerator? en = null;
        IMMDeviceCollection? col = null;
        try
        {
            en = CreateEnumerator();
            if (en == null) return list;

            string defaultId = string.Empty;
            if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var defDev) == 0 && defDev != null)
            {
                try { defDev.GetId(out defaultId); }
                finally { ReleaseCom(defDev); }
            }

            // DEVICE_STATE_ACTIVE = 0x1
            if (en.EnumAudioEndpoints(EDataFlow.eRender, 1, out col) != 0 || col == null) return list;
            col.GetCount(out uint count);

            var pkeyFriendly = new PROPERTYKEY { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? dev = null;
                IPropertyStore? props = null;
                try
                {
                    if (col.Item(i, out dev) != 0 || dev == null) continue;
                    dev.GetId(out var id);
                    string name = $"Устройство #{i + 1}";
                    try
                    {
                        if (dev.OpenPropertyStore(STGM_READ, out props) == 0 && props != null && props.GetValue(ref pkeyFriendly, out var pv) == 0)
                        {
                            if (pv.vt == 31 && pv.p1 != IntPtr.Zero) // VT_LPWSTR
                                name = Marshal.PtrToStringUni(pv.p1) ?? name;
                            PropVariantClear(ref pv);
                        }
                    }
                    catch { }

                    bool isHeadphones = name.Contains("Headphone", StringComparison.OrdinalIgnoreCase)
                                     || name.Contains("Headset", StringComparison.OrdinalIgnoreCase)
                                     || name.Contains("Наушник", StringComparison.OrdinalIgnoreCase)
                                     || name.Contains("Гарнитур", StringComparison.OrdinalIgnoreCase)
                                     || name.Contains("AirPods", StringComparison.OrdinalIgnoreCase)
                                     || name.Contains("Ear", StringComparison.OrdinalIgnoreCase);

                    list.Add(new AudioDeviceItem
                    {
                        Id = id,
                        Name = name,
                        IsDefault = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase),
                        IsHeadphones = isHeadphones
                    });
                }
                finally
                {
                    ReleaseCom(props);
                    ReleaseCom(dev);
                }
            }
        }
        catch (Exception ex)
        {
            LoggerService.Error("Failed to enumerate playback devices", ex);
        }
        finally
        {
            ReleaseCom(col);
            ReleaseCom(en);
        }
        return list;
    }

    public static string? GetActiveCaptureProcessName()
    {
        IMMDeviceEnumerator? en = null;
        IMMDeviceCollection? col = null;
        try
        {
            en = CreateEnumerator();
            if (en == null) return null;

            // Enumerate all active capture devices (microphone, headset mic, line-in)
            if (en.EnumAudioEndpoints(EDataFlow.eCapture, 1 /* DEVICE_STATE_ACTIVE */, out col) != 0 || col == null)
                return null;

            col.GetCount(out uint devCount);
            var iidMgr = IID_IAudioSessionManager2;
            uint ownPid = (uint)Environment.ProcessId;

            for (uint d = 0; d < devCount; d++)
            {
                IMMDevice? dev = null;
                object? mgrObj = null;
                IAudioSessionEnumerator? sessionEnum = null;
                try
                {
                    if (col.Item(d, out dev) != 0 || dev == null) continue;
                    if (dev.Activate(ref iidMgr, CLSCTX_ALL, IntPtr.Zero, out mgrObj) != 0 || mgrObj is not IAudioSessionManager2 mgr)
                        continue;

                    if (mgr.GetSessionEnumerator(out sessionEnum) != 0 || sessionEnum == null)
                        continue;

                    sessionEnum.GetCount(out int count);
                    for (int i = 0; i < count; i++)
                    {
                        IAudioSessionControl? ctrl = null;
                        try
                        {
                            if (sessionEnum.GetSession(i, out ctrl) != 0 || ctrl == null)
                                continue;

                            if (ctrl.GetState(out int state) == 0 && state == 1 /* AudioSessionStateActive */ &&
                                ctrl is IAudioSessionControl2 c2 &&
                                c2.GetProcessId(out uint pid) == 0 && pid != 0 && pid != ownPid)
                            {
                                try
                                {
                                    using var proc = Process.GetProcessById((int)pid);
                                    if (!string.IsNullOrWhiteSpace(proc.ProcessName))
                                        return proc.ProcessName;
                                }
                                catch { }
                            }
                        }
                        finally
                        {
                            ReleaseCom(ctrl);
                        }
                    }
                }
                catch { }
                finally
                {
                    ReleaseCom(sessionEnum);
                    ReleaseCom(mgrObj);
                    ReleaseCom(dev);
                }
            }
        }
        catch { }
        finally
        {
            ReleaseCom(col);
            ReleaseCom(en);
        }
        return null;
    }

    public static bool SetDefaultPlaybackDevice(string deviceId)
    {
        object? obj = null;
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_PolicyConfigClient);
            if (type == null) return false;
            obj = Activator.CreateInstance(type);
            if (obj is IPolicyConfig pc)
            {
                pc.SetDefaultEndpoint(deviceId, ERole.eConsole);
                pc.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
                pc.SetDefaultEndpoint(deviceId, ERole.eCommunications);
                InvalidateRenderMeterCache();
                return true;
            }
        }
        catch (Exception ex)
        {
            LoggerService.Error($"Failed to switch audio device {deviceId}", ex);
        }
        finally
        {
            ReleaseCom(obj);
        }
        return false;
    }

    public static string GetDefaultPlaybackDeviceId()
    {
        IMMDeviceEnumerator? en = null;
        IMMDevice? defDev = null;
        try
        {
            en = CreateEnumerator();
            if (en != null && en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out defDev) == 0 && defDev != null)
            {
                defDev.GetId(out var id);
                return id ?? string.Empty;
            }
        }
        catch { }
        finally
        {
            ReleaseCom(defDev);
            ReleaseCom(en);
        }
        return string.Empty;
    }

    public static List<AudioSessionItem> GetAudioRenderSessions()
    {
        var items = new List<AudioSessionItem>();
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        object? obj = null;
        IAudioSessionEnumerator? sessionEnum = null;
        try
        {
            en = CreateEnumerator();
            if (en == null) return items;
            if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out dev) != 0 || dev == null)
                return items;

            var iidMgr = IID_IAudioSessionManager2;
            if (dev.Activate(ref iidMgr, CLSCTX_ALL, IntPtr.Zero, out obj) != 0 || obj is not IAudioSessionManager2 mgr)
                return items;

            if (mgr.GetSessionEnumerator(out sessionEnum) != 0 || sessionEnum == null)
                return items;

            sessionEnum.GetCount(out int count);
            int ownPid = Environment.ProcessId;
            var seenPids = new HashSet<int>();

            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl? ctrl = null;
                try
                {
                    if (sessionEnum.GetSession(i, out ctrl) != 0 || ctrl == null)
                        continue;

                    if (ctrl.GetState(out int state) != 0 || state == 2 /* AudioSessionStateExpired */)
                        continue;

                    if (ctrl is IAudioSessionControl2 c2 && c2.GetProcessId(out uint pid) == 0 && pid != 0 && pid != ownPid)
                    {
                        if (!seenPids.Add((int)pid)) continue;
                        string procName = $"PID {pid}";
                        try
                        {
                            using var proc = Process.GetProcessById((int)pid);
                            if (!string.IsNullOrWhiteSpace(proc.ProcessName))
                                procName = proc.ProcessName;
                        }
                        catch { continue; }

                        int volPct = 100;
                        bool muted = false;
                        if (ctrl is ISimpleAudioVolume sav)
                        {
                            sav.GetMasterVolume(out float lvl);
                            sav.GetMute(out muted);
                            volPct = Math.Clamp((int)Math.Round(lvl * 100f), 0, 100);
                        }

                        items.Add(new AudioSessionItem
                        {
                            Pid = (int)pid,
                            ProcessName = procName,
                            VolumePercent = volPct,
                            IsMuted = muted
                        });
                    }
                }
                finally
                {
                    ReleaseCom(ctrl);
                }
            }
        }
        catch { }
        finally
        {
            ReleaseCom(sessionEnum);
            ReleaseCom(obj);
            ReleaseCom(dev);
            ReleaseCom(en);
        }
        return items.OrderBy(a => a.ProcessName).Take(6).ToList();
    }

    public static void SetAppSessionVolume(int pid, int percent)
    {
        OperateOnAppSession(pid, sav =>
        {
            var ctx = Guid.Empty;
            sav.SetMasterVolume(Math.Clamp(percent / 100f, 0f, 1f), ref ctx);
            if (percent > 0) sav.SetMute(false, ref ctx);
        });
    }

    public static bool ToggleAppSessionMute(int pid)
    {
        bool result = false;
        OperateOnAppSession(pid, sav =>
        {
            sav.GetMute(out bool muted);
            var ctx = Guid.Empty;
            sav.SetMute(!muted, ref ctx);
            result = !muted;
        });
        return result;
    }

    public static bool IsProcessMuted(string processKeyword)
    {
        bool found = false;
        bool anyMuted = false;
        OperateOnProcessNameSessions(processKeyword, sav =>
        {
            found = true;
            sav.GetMute(out bool m);
            if (m) anyMuted = true;
        });
        return found && anyMuted;
    }

    public static bool SetProcessMute(string processKeyword, bool mute)
    {
        bool touched = false;
        OperateOnProcessNameSessions(processKeyword, sav =>
        {
            var ctx = Guid.Empty;
            sav.SetMute(mute, ref ctx);
            touched = true;
        });
        return touched;
    }

    private static void OperateOnProcessNameSessions(string processKeyword, Action<ISimpleAudioVolume> action)
    {
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        object? obj = null;
        IAudioSessionEnumerator? sessionEnum = null;
        try
        {
            en = CreateEnumerator();
            if (en == null) return;
            if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out dev) != 0 || dev == null)
                return;

            var iidMgr = IID_IAudioSessionManager2;
            if (dev.Activate(ref iidMgr, CLSCTX_ALL, IntPtr.Zero, out obj) != 0 || obj is not IAudioSessionManager2 mgr)
                return;

            if (mgr.GetSessionEnumerator(out sessionEnum) != 0 || sessionEnum == null)
                return;

            sessionEnum.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl? ctrl = null;
                try
                {
                    if (sessionEnum.GetSession(i, out ctrl) == 0 &&
                        ctrl is IAudioSessionControl2 c2 &&
                        c2.GetProcessId(out uint pid) == 0 &&
                        pid > 0 &&
                        ctrl is ISimpleAudioVolume sav)
                    {
                        try
                        {
                            using var p = Process.GetProcessById((int)pid);
                            if (p.ProcessName.Contains(processKeyword, StringComparison.OrdinalIgnoreCase))
                                action(sav);
                        }
                        catch { }
                    }
                }
                finally
                {
                    ReleaseCom(ctrl);
                }
            }
        }
        catch { }
        finally
        {
            ReleaseCom(sessionEnum);
            ReleaseCom(obj);
            ReleaseCom(dev);
            ReleaseCom(en);
        }
    }

    private static void OperateOnAppSession(int targetPid, Action<ISimpleAudioVolume> action)
    {
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        object? obj = null;
        IAudioSessionEnumerator? sessionEnum = null;
        try
        {
            en = CreateEnumerator();
            if (en == null) return;
            if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out dev) != 0 || dev == null)
                return;

            var iidMgr = IID_IAudioSessionManager2;
            if (dev.Activate(ref iidMgr, CLSCTX_ALL, IntPtr.Zero, out obj) != 0 || obj is not IAudioSessionManager2 mgr)
                return;

            if (mgr.GetSessionEnumerator(out sessionEnum) != 0 || sessionEnum == null)
                return;

            sessionEnum.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl? ctrl = null;
                try
                {
                    if (sessionEnum.GetSession(i, out ctrl) == 0 &&
                        ctrl is IAudioSessionControl2 c2 &&
                        c2.GetProcessId(out uint pid) == 0 &&
                        (int)pid == targetPid &&
                        ctrl is ISimpleAudioVolume sav)
                    {
                        action(sav);
                    }
                }
                finally
                {
                    ReleaseCom(ctrl);
                }
            }
        }
        catch { }
        finally
        {
            ReleaseCom(sessionEnum);
            ReleaseCom(obj);
            ReleaseCom(dev);
            ReleaseCom(en);
        }
    }
}
