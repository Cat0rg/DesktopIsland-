using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DesktopIsland.Models;

namespace DesktopIsland.Services;

public class SystemMonitorService
{
    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private bool _hasPrevCpu;

    private long _prevBytesRecv;
    private long _prevBytesSent;
    private DateTime _prevNetTime;
    private bool _hasPrevNet;

    private ulong _prevDwmFrame;
    private DateTime _prevFpsTime;
    private bool _hasPrevFps;
    private readonly EtwFpsService _etwFps = new();
    private bool _etwStarted;

    private string? _nvidiaSmiPath;
    private bool _checkedNvidiaSmi;
    private DateTime _lastGpuPollAt = DateTime.MinValue;
    private double? _lastGpuPercent;
    private double? _lastGpuTempC;
    private PerformanceCounter? _gpuCounter;
    private bool _gpuCounterInitFailed;

    public bool IsEtwFpsActive => _etwFps.IsAvailable;
    public void StopFpsMonitor() => _etwFps.Dispose();

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UNSIGNED_RATIO
    {
        public uint uiNumerator;
        public uint uiDenominator;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DWM_TIMING_INFO
    {
        public int cbSize;
        public UNSIGNED_RATIO rateRefresh;
        public ulong qpcRefreshPeriod;
        public UNSIGNED_RATIO rateCompose;
        public ulong qpcVBlank;
        public ulong cRefresh;
        public uint cDXRefresh;
        public ulong qpcCompose;
        public ulong cFrame;
        public uint cDXPresent;
        public ulong cRefreshFrame;
        public ulong cFrameSubmitted;
        public uint cDXPresentSubmitted;
        public ulong cFrameConfirmed;
        public uint cDXPresentConfirmed;
        public ulong cRefreshConfirmed;
        public uint cDXRefreshConfirmed;
        public ulong cFramesLate;
        public uint cFramesOutstanding;
        public ulong cFrameDisplayed;
        public ulong qpcFrameDisplayed;
        public ulong cRefreshFrameDisplayed;
        public ulong cFrameComplete;
        public ulong qpcFrameComplete;
        public ulong cFramePending;
        public ulong qpcFramePending;
        public ulong cFramesDisplayed;
        public ulong cFramesComplete;
        public ulong cFramesPending;
        public ulong cFramesAvailable;
        public ulong cFramesDropped;
        public ulong cFramesMissed;
        public ulong cRefreshNextDisplayed;
        public ulong cRefreshNextPresented;
        public ulong cRefreshesDisplayed;
        public ulong cRefreshesPresented;
        public ulong cRefreshStarted;
        public ulong cPixelsReceived;
        public ulong cPixelsDrawn;
        public ulong cBuffersEmpty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
        public ulong ToUInt64() => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DWM_TIMING_INFO pTimingInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public SystemMetrics SampleSystemAndNetwork(bool includeGpu = false)
    {
        var metrics = new SystemMetrics();

        // CPU
        if (GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
        {
            ulong idle = idleFt.ToUInt64();
            ulong kernel = kernelFt.ToUInt64();
            ulong user = userFt.ToUInt64();

            if (_hasPrevCpu)
            {
                ulong idleDiff = idle - _prevIdle;
                ulong totalDiff = (kernel - _prevKernel) + (user - _prevUser);
                if (totalDiff > 0)
                {
                    double cpu = (1.0 - (double)idleDiff / totalDiff) * 100.0;
                    metrics.CpuPercent = Math.Clamp(cpu, 0, 100);
                }
            }

            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            _hasPrevCpu = true;
        }

        // RAM
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            const double gb = 1024.0 * 1024.0 * 1024.0;
            metrics.RamTotalGb = mem.ullTotalPhys / gb;
            metrics.RamFreeGb = mem.ullAvailPhys / gb;
            metrics.RamUsedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / gb;
            metrics.RamPercent = mem.dwMemoryLoad;
        }

        // Network
        try
        {
            long totalRecv = 0;
            long totalSent = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                var stats = ni.GetIPv4Statistics();
                totalRecv += stats.BytesReceived;
                totalSent += stats.BytesSent;
            }

            var now = DateTime.UtcNow;
            if (_hasPrevNet)
            {
                double secs = (now - _prevNetTime).TotalSeconds;
                if (secs > 0.1)
                {
                    metrics.NetDownBytesPerSec = Math.Max(0, (totalRecv - _prevBytesRecv) / secs);
                    metrics.NetUpBytesPerSec = Math.Max(0, (totalSent - _prevBytesSent) / secs);
                }
            }
            _prevBytesRecv = totalRecv;
            _prevBytesSent = totalSent;
            _prevNetTime = now;
            _hasPrevNet = true;
        }
        catch
        {
            // Ignore transient NIC enumeration error
        }

        // Active Window + ETW/DWM FPS
        try
        {
            if (!_etwStarted)
            {
                _etwStarted = true;
                _etwFps.Start();
            }

            uint fgPid = 0;
            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                GetWindowThreadProcessId(fg, out fgPid);
                metrics.ActivePid = fgPid;
                if (fgPid > 0)
                {
                    using var p = System.Diagnostics.Process.GetProcessById((int)fgPid);
                    metrics.ActiveWindowTitle = p.ProcessName;
                }
            }

            var etwFps = _etwFps.GetPidFps(fgPid);
            if (etwFps.HasValue && etwFps.Value > 0)
            {
                metrics.ActiveFps = etwFps.Value;
            }
            else
            {
                var timing = new DWM_TIMING_INFO { cbSize = Marshal.SizeOf<DWM_TIMING_INFO>() };
                if (DwmGetCompositionTimingInfo(IntPtr.Zero, ref timing) == 0)
                {
                    var now = DateTime.UtcNow;
                    double maxHz = timing.rateRefresh.uiDenominator > 0
                        ? timing.rateRefresh.uiNumerator / (double)timing.rateRefresh.uiDenominator
                        : 240.0;

                    if (_hasPrevFps)
                    {
                        double secs = (now - _prevFpsTime).TotalSeconds;
                        if (secs > 0.1 && timing.cFrame >= _prevDwmFrame)
                        {
                            double rawFps = (timing.cFrame - _prevDwmFrame) / secs;
                            metrics.ActiveFps = Math.Clamp(rawFps, 0, Math.Max(60.0, maxHz));
                        }
                    }
                    _prevDwmFrame = timing.cFrame;
                    _prevFpsTime = now;
                    _hasPrevFps = true;
                }
            }
        }
        catch { }

        metrics.PingMs = _lastPingMs;
        metrics.PingJitterMs = PingJitterMs;
        metrics.PacketLossPercent = PacketLossPercent;
        if (includeGpu)
            SampleGpuMetrics(metrics);
        return metrics;
    }

    private void SampleGpuMetrics(SystemMetrics metrics)
    {
        if ((DateTime.UtcNow - _lastGpuPollAt).TotalSeconds < 1.5)
        {
            metrics.GpuPercent = _lastGpuPercent;
            metrics.GpuTempC = _lastGpuTempC;
            return;
        }
        _lastGpuPollAt = DateTime.UtcNow;

        if (!_checkedNvidiaSmi)
        {
            _checkedNvidiaSmi = true;
            var candidates = new[]
            {
                Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
                @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe"
            };
            _nvidiaSmiPath = candidates.FirstOrDefault(File.Exists);
        }

        if (_nvidiaSmiPath != null)
        {
            try
            {
                var psi = new ProcessStartInfo(_nvidiaSmiPath, "--query-gpu=utilization.gpu,temperature.gpu --format=csv,noheader,nounits")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadLine();
                    proc.WaitForExit(900);
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        var parts = output.Split(',');
                        if (parts.Length >= 2 &&
                            double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double gpuPct) &&
                            double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double tempC))
                        {
                            _lastGpuPercent = Math.Clamp(gpuPct, 0, 100);
                            _lastGpuTempC = Math.Clamp(tempC, 0, 130);
                            metrics.GpuPercent = _lastGpuPercent;
                            metrics.GpuTempC = _lastGpuTempC;
                            return;
                        }
                    }
                }
            }
            catch { }
        }

        // Universal fallback for non-NVIDIA GPUs using Windows GPU Engine performance counter
        if (!_gpuCounterInitFailed)
        {
            try
            {
                if (_gpuCounter == null)
                {
                    var cat = new PerformanceCounterCategory("GPU Engine");
                    var names = cat.GetInstanceNames();
                    var best = names.FirstOrDefault(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase));
                    if (best != null)
                    {
                        _gpuCounter = new PerformanceCounter("GPU Engine", "Utilization Percentage", best, true);
                        _gpuCounter.NextValue();
                    }
                    else _gpuCounterInitFailed = true;
                }
                else
                {
                    _lastGpuPercent = Math.Clamp(_gpuCounter.NextValue(), 0, 100);
                }
            }
            catch { _gpuCounterInitFailed = true; }
        }

        metrics.GpuPercent = _lastGpuPercent;
        metrics.GpuTempC = _lastGpuTempC;
    }

    public List<DriveMetric> SampleDrives()
    {
        var list = new List<DriveMetric>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives()
                         .Where(d => d.IsReady && d.TotalSize > 0)
                         .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                const double gb = 1024.0 * 1024.0 * 1024.0;
                double total = drive.TotalSize / gb;
                double free = drive.TotalFreeSpace / gb;
                double used = Math.Max(0, total - free);
                list.Add(new DriveMetric
                {
                    Name = drive.Name.TrimEnd('\\'),
                    RootPath = drive.Name,
                    Label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Локальный" : drive.VolumeLabel,
                    TotalGb = total,
                    FreeGb = free,
                    UsedGb = used,
                    UsedPercent = total > 0 ? (used / total) * 100.0 : 0
                });
            }
        }
        catch (Exception ex)
        {
            LoggerService.Error("Failed to sample drives.", ex);
        }
        return list;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int dwOutBufLen,
        bool sort,
        int ipVersion,
        int tblClass,
        uint reserved = 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint Th32csSnapProcess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    private int _lastPingMs = -1;
    private DateTime _lastPingAt = DateTime.MinValue;
    private bool _pingInFlight;
    private readonly Queue<int> _recentPingWindow = new();

    public int LastPingMs => _lastPingMs;
    public int PingJitterMs { get; private set; }
    public int PacketLossPercent { get; private set; }
    public bool IsGameServerPing { get; private set; }
    public string PingTargetLabel { get; private set; } = "1.1.1.1";

    private void RecordPingSample(int rttMs)
    {
        if (_recentPingWindow.Count >= 8)
            _recentPingWindow.Dequeue();
        _recentPingWindow.Enqueue(rttMs);

        var samples = _recentPingWindow.ToArray();
        int lost = samples.Count(s => s < 0);
        PacketLossPercent = samples.Length > 0 ? (int)Math.Round(lost * 100.0 / samples.Length) : 0;

        var valid = samples.Where(s => s > 0).ToArray();
        if (valid.Length >= 2)
        {
            double diffSum = 0;
            for (int i = 1; i < valid.Length; i++)
                diffSum += Math.Abs(valid[i] - valid[i - 1]);
            PingJitterMs = (int)Math.Round(diffSum / (valid.Length - 1));
        }
        else
        {
            PingJitterMs = 0;
        }
    }

    private static HashSet<uint> CollectProcessTreePids(uint rootPid)
    {
        var pids = new HashSet<uint> { rootPid };
        try
        {
            string? rootName = null;
            try
            {
                using var fgProc = Process.GetProcessById((int)rootPid);
                rootName = fgProc.ProcessName;
            }
            catch { }

            IntPtr snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snap != IntPtr.Zero && snap.ToInt64() != -1)
            {
                try
                {
                    var all = new List<(uint Pid, uint Parent, string Name)>();
                    var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                    if (Process32First(snap, ref entry))
                    {
                        do
                        {
                            string exeName = Path.GetFileNameWithoutExtension(entry.szExeFile ?? string.Empty);
                            all.Add((entry.th32ProcessID, entry.th32ParentProcessID, exeName));
                            if (!string.IsNullOrEmpty(rootName) && exeName.Equals(rootName, StringComparison.OrdinalIgnoreCase))
                                pids.Add(entry.th32ProcessID);
                        }
                        while (Process32Next(snap, ref entry));
                    }

                    // Expand children up to 2 hops
                    for (int hop = 0; hop < 2; hop++)
                    {
                        foreach (var p in all)
                        {
                            if (pids.Contains(p.Parent))
                                pids.Add(p.Pid);
                        }
                    }
                }
                finally
                {
                    CloseHandle(snap);
                }
            }
        }
        catch { }
        return pids;
    }

    private static List<(string Ip, int Port)> FindGameServerEndpoints(uint activePid)
    {
        var result = new List<(string Ip, int Port)>();
        if (activePid == 0) return result;
        try
        {
            var targetPids = CollectProcessTreePids(activePid);

            int bufSize = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref bufSize, false, 2 /* AF_INET */, 4 /* TCP_TABLE_OWNER_PID_CONNECTIONS */);
            if (bufSize <= 0) return result;

            IntPtr mem = Marshal.AllocHGlobal(bufSize);
            try
            {
                if (GetExtendedTcpTable(mem, ref bufSize, false, 2, 4) != 0)
                    return result;

                int count = Marshal.ReadInt32(mem);
                int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                IntPtr rowPtr = IntPtr.Add(mem, 4);

                var seen = new HashSet<string>(StringComparer.Ordinal);
                var nonWeb = new List<(string Ip, int Port)>();
                var webFallback = new List<(string Ip, int Port)>();

                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    rowPtr = IntPtr.Add(rowPtr, rowSize);

                    // 5 = MIB_TCP_STATE_ESTAB
                    if (row.dwState != 5 || !targetPids.Contains(row.dwOwningPid))
                        continue;

                    var ip = new IPAddress(row.dwRemoteAddr);
                    byte[] b = ip.GetAddressBytes();
                    if (b.Length != 4) continue;
                    // Skip loopback, 0.0.0.0, link-local, RFC1918 private LAN, multicast
                    if (b[0] == 0 || b[0] == 127 || b[0] == 10 || b[0] >= 224) continue;
                    if (b[0] == 169 && b[1] == 254) continue;
                    if (b[0] == 192 && b[1] == 168) continue;
                    if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) continue;

                    int port = (ushort)IPAddress.NetworkToHostOrder((short)(row.dwRemotePort & 0xFFFF));
                    string ipStr = ip.ToString();
                    string key = $"{ipStr}:{port}";
                    if (!seen.Add(key)) continue;

                    if (port != 80 && port != 443)
                        nonWeb.Add((ipStr, port));
                    else
                        webFallback.Add((ipStr, port));
                }

                result.AddRange(nonWeb);
                result.AddRange(webFallback);
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
            }
        }
        catch { }
        return result;
    }

    private static async Task<int> MeasureTcpConnectRttAsync(string ip, int port, int timeoutMs = 850)
    {
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var cts = new CancellationTokenSource(timeoutMs);
            var sw = Stopwatch.StartNew();
            await sock.ConnectAsync(ip, port, cts.Token);
            sw.Stop();
            return Math.Max(1, (int)Math.Round(sw.Elapsed.TotalMilliseconds));
        }
        catch
        {
            return -1;
        }
    }

    public async Task<int> SamplePingAsync(uint activePid = 0, bool isGame = false)
    {
        if (_pingInFlight || (DateTime.UtcNow - _lastPingAt).TotalSeconds < 2.5)
            return _lastPingMs;

        _pingInFlight = true;
        try
        {
            var gameEndpoints = isGame ? FindGameServerEndpoints(activePid) : new List<(string Ip, int Port)>();
            if (gameEndpoints.Count > 0)
            {
                using var ping = new Ping();
                foreach (var ep in gameEndpoints.Take(3))
                {
                    try
                    {
                        var reply = await ping.SendPingAsync(ep.Ip, 850);
                        if (reply.Status == IPStatus.Success)
                        {
                            _lastPingMs = Math.Max(1, (int)reply.RoundtripTime);
                            RecordPingSample(_lastPingMs);
                            IsGameServerPing = true;
                            PingTargetLabel = $"Сервер игры (ICMP {ep.Ip})";
                            _lastPingAt = DateTime.UtcNow;
                            return _lastPingMs;
                        }
                    }
                    catch { }
                }

                // Many game relays/servers block ICMP: measure direct TCP handshake RTT to their active remote endpoint
                foreach (var ep in gameEndpoints.Take(3))
                {
                    int tcpMs = await MeasureTcpConnectRttAsync(ep.Ip, ep.Port, 850);
                    if (tcpMs > 0)
                    {
                        _lastPingMs = tcpMs;
                        RecordPingSample(_lastPingMs);
                        IsGameServerPing = true;
                        PingTargetLabel = $"Сервер игры (TCP {ep.Ip}:{ep.Port})";
                        _lastPingAt = DateTime.UtcNow;
                        return _lastPingMs;
                    }
                }
            }

            using (var netPing = new Ping())
            {
                var reply = await netPing.SendPingAsync("1.1.1.1", 1200);
                _lastPingMs = reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : -1;
                RecordPingSample(_lastPingMs);
            }
            IsGameServerPing = false;
            PingTargetLabel = gameEndpoints.Count == 0
                ? (isGame ? "Сеть 1.1.1.1 (игра на чистом UDP без TCP-релея)" : "Сеть (1.1.1.1)")
                : $"Сеть 1.1.1.1 ({gameEndpoints[0].Ip} скрывает ICMP/TCP)";
            _lastPingAt = DateTime.UtcNow;
        }
        catch
        {
            _lastPingMs = -1;
            RecordPingSample(-1);
            IsGameServerPing = false;
            PingTargetLabel = "1.1.1.1";
            _lastPingAt = DateTime.UtcNow;
        }
        finally
        {
            _pingInFlight = false;
        }
        return _lastPingMs;
    }

    public List<ProcessMetricItem> GetTopProcesses(int count = 3)
    {
        var result = new List<ProcessMetricItem>();
        try
        {
            var procs = Process.GetProcesses();
            var items = new List<ProcessMetricItem>(procs.Length);
            foreach (var p in procs)
            {
                try
                {
                    if (p.Id <= 4) continue;
                    string name = p.ProcessName;
                    if (name.Equals("Memory Compression", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Registry", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Secure System", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("System", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Idle", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    double mb = p.WorkingSet64 / (1024.0 * 1024.0);
                    if (mb < 20) continue;
                    items.Add(new ProcessMetricItem
                    {
                        Pid = p.Id,
                        Name = name,
                        MemoryMb = mb
                    });
                }
                catch { }
                finally { p.Dispose(); }
            }

            result = items
                .OrderByDescending(i => i.MemoryMb)
                .Take(count)
                .ToList();
        }
        catch (Exception ex)
        {
            LoggerService.Error("Failed to sample top processes.", ex);
        }
        return result;
    }

    public bool KillProcess(int pid)
    {
        try
        {
            if (pid == Environment.ProcessId || pid <= 4) return false;
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            LoggerService.Info($"Killed process {p.ProcessName} (PID {pid}) from System tab.");
            return true;
        }
        catch (Exception ex)
        {
            LoggerService.Error($"Failed to kill PID {pid}.", ex);
            return false;
        }
    }

    public List<RecentDownloadItem> GetRecentDownloads(int count = 4)
    {
        var list = new List<RecentDownloadItem>();
        try
        {
            var downloadsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            if (!Directory.Exists(downloadsPath)) return list;

            var files = new DirectoryInfo(downloadsPath)
                .EnumerateFiles()
                .Where(f => !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                            !f.Name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) &&
                            !f.Attributes.HasFlag(FileAttributes.Hidden))
                .OrderByDescending(f => f.LastWriteTime)
                .Take(count);

            foreach (var f in files)
            {
                list.Add(new RecentDownloadItem
                {
                    Name = f.Name,
                    FullPath = f.FullName,
                    SizeFormatted = CleanupCategory.FormatBytes(f.Length),
                    TimeLabel = f.LastWriteTime.ToString("dd.MM HH:mm")
                });
            }
        }
        catch { }
        return list;
    }

    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024)
            return $"{bytesPerSec / (1024 * 1024):F1} МБ/с";
        if (bytesPerSec >= 1024)
            return $"{bytesPerSec / 1024:F0} КБ/с";
        return $"{bytesPerSec:F0} Б/с";
    }
}
