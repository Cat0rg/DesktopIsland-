using System;
using System.Collections.Generic;
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

    private static IMMDeviceEnumerator? CreateEnumerator()
    {
        var type = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
        return type is null ? null : Activator.CreateInstance(type) as IMMDeviceEnumerator;
    }

    public static int GetMasterVolumePercent()
    {
        try
        {
            var en = CreateEnumerator();
            if (en == null) return 0;
            if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var dev) != 0) return 0;
            var iid = IID_IAudioEndpointVolume;
            dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj);
            if (obj is IAudioEndpointVolume vol)
            {
                vol.GetMasterVolumeLevelScalar(out float level);
                return Math.Clamp((int)Math.Round(level * 100f), 0, 100);
            }
        }
        catch { }
        return 0;
    }

    public static int ChangeMasterVolume(int deltaPercent)
    {
        try
        {
            var en = CreateEnumerator();
            if (en == null) return 0;
            if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var dev) != 0) return 0;
            var iid = IID_IAudioEndpointVolume;
            dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj);
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
        return GetMasterVolumePercent();
    }

    public static void SetMasterVolumePercent(int percent)
    {
        try
        {
            var en = CreateEnumerator();
            if (en == null) return;
            if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var dev) != 0) return;
            var iid = IID_IAudioEndpointVolume;
            dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj);
            if (obj is IAudioEndpointVolume vol)
            {
                float next = Math.Clamp(percent / 100f, 0f, 1f);
                var ctx = Guid.Empty;
                vol.SetMasterVolumeLevelScalar(next, ref ctx);
                if (next > 0) vol.SetMute(false, ref ctx);
            }
        }
        catch { }
    }

    public static float GetRenderPeakLevel()
    {
        try
        {
            var en = CreateEnumerator();
            if (en == null) return 0f;
            if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var dev) != 0) return 0f;
            var iid = IID_IAudioMeterInformation;
            dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj);
            if (obj is IAudioMeterInformation meter)
            {
                meter.GetPeakValue(out float peak);
                return Math.Clamp(peak, 0f, 1f);
            }
        }
        catch { }
        return 0f;
    }

    public static bool IsMicMuted()
    {
        try
        {
            var en = CreateEnumerator();
            if (en == null) return false;
            if (en.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eCommunications, out var dev) != 0)
            {
                if (en.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out dev) != 0) return false;
            }
            var iid = IID_IAudioEndpointVolume;
            dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj);
            if (obj is IAudioEndpointVolume vol)
            {
                vol.GetMute(out bool muted);
                return muted;
            }
        }
        catch { }
        return false;
    }

    public static bool ToggleMicMute()
    {
        try
        {
            var en = CreateEnumerator();
            if (en == null) return false;
            if (en.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eCommunications, out var dev) != 0)
            {
                if (en.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out dev) != 0) return false;
            }
            var iid = IID_IAudioEndpointVolume;
            dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj);
            if (obj is IAudioEndpointVolume vol)
            {
                vol.GetMute(out bool muted);
                var ctx = Guid.Empty;
                vol.SetMute(!muted, ref ctx);
                return !muted;
            }
        }
        catch { }
        return false;
    }

    public static List<AudioDeviceItem> GetPlaybackDevices()
    {
        var list = new List<AudioDeviceItem>();
        try
        {
            var en = CreateEnumerator();
            if (en == null) return list;

            string defaultId = string.Empty;
            if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var defDev) == 0)
                defDev.GetId(out defaultId);

            // DEVICE_STATE_ACTIVE = 0x1
            if (en.EnumAudioEndpoints(EDataFlow.eRender, 1, out var col) != 0) return list;
            col.GetCount(out uint count);

            var pkeyFriendly = new PROPERTYKEY { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };
            for (uint i = 0; i < count; i++)
            {
                col.Item(i, out var dev);
                dev.GetId(out var id);
                string name = $"Устройство #{i + 1}";
                try
                {
                    if (dev.OpenPropertyStore(STGM_READ, out var props) == 0 && props.GetValue(ref pkeyFriendly, out var pv) == 0)
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
        }
        catch (Exception ex)
        {
            LoggerService.Error("Failed to enumerate playback devices", ex);
        }
        return list;
    }

    public static bool SetDefaultPlaybackDevice(string deviceId)
    {
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_PolicyConfigClient);
            if (type == null) return false;
            var obj = Activator.CreateInstance(type);
            if (obj is IPolicyConfig pc)
            {
                pc.SetDefaultEndpoint(deviceId, ERole.eConsole);
                pc.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
                pc.SetDefaultEndpoint(deviceId, ERole.eCommunications);
                return true;
            }
        }
        catch (Exception ex)
        {
            LoggerService.Error($"Failed to switch audio device {deviceId}", ex);
        }
        return false;
    }
}
