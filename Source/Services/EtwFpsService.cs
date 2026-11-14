using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace DesktopIsland.Services;

/// <summary>
/// Lightweight zero-injection ETW frame counter using Windows DXGI / DxgKrnl Present events.
/// Requires either Administrator privileges or membership in the "Performance Log Users" group.
/// Gracefully falls back (IsAvailable = false) when access is denied.
/// </summary>
public sealed class EtwFpsService : IDisposable
{
    private const string SessionName = "DesktopIsland_DXGI_FPS_v1";
    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;
    private const ulong INVALID_PROCESSTRACE_HANDLE = ulong.MaxValue;

    private static readonly Guid DxgiProvider = new("ca11c036-0102-4a2d-a6ad-f03cfed5d3c9");
    private static readonly Guid DxgKrnlProvider = new("802ec45a-1e99-4b83-9920-87c98277ba9d");

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_RECORD
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
        public uint KernelTime;
        public uint UserTime;
        public Guid ActivityId;
    }

    private delegate void EventRecordCallback(ref EVENT_RECORD eventRecord);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EVENT_TRACE_LOGFILEW
    {
        public string? LogFileName;
        public string? LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 88)]
        public byte[]? CurrentEvent; // EVENT_TRACE
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 280)]
        public byte[]? LogfileHeader; // TRACE_LOGFILE_HEADER (280 bytes on x64)
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTrace(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTrace(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll")]
    private static extern uint EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode, byte level, ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern ulong OpenTrace(ref EVENT_TRACE_LOGFILEW logfile);

    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong traceHandle);

    private readonly ConcurrentDictionary<uint, Queue<long>> _dxgiFramesByPid = new();
    private readonly ConcurrentDictionary<uint, Queue<long>> _dxgFramesByPid = new();
    private readonly object _sync = new();
    private Thread? _worker;
    private ulong _sessionHandle;
    private ulong _traceHandle = INVALID_PROCESSTRACE_HANDLE;
    private EventRecordCallback? _callbackKeepAlive;
    private bool _disposed;

    public bool IsAvailable { get; private set; }

    public void Start()
    {
        if (_disposed || _worker != null) return;

        IntPtr propsMem = AllocTraceProperties();
        try
        {
            // Stop any leftover session with same name first
            ControlTrace(0, SessionName, propsMem, EVENT_TRACE_CONTROL_STOP);

            Marshal.WriteInt32(propsMem, 0, 2048);
            Marshal.WriteInt32(propsMem, 44, unchecked((int)WNODE_FLAG_TRACED_GUID));
            Marshal.WriteInt32(propsMem, 64, (int)EVENT_TRACE_REAL_TIME_MODE);
            Marshal.WriteInt32(propsMem, 116, 120);

            uint rc = StartTrace(out _sessionHandle, SessionName, propsMem);
            if (rc != 0)
            {
                LoggerService.Info($"ETW FPS disabled: StartTrace rc={rc} (requires Performance Log Users or Admin)");
                IsAvailable = false;
                return;
            }

            Guid dxgi = DxgiProvider;
            Guid dxg = DxgKrnlProvider;
            EnableTraceEx2(_sessionHandle, ref dxgi, 1, 4, 0xFFFFFFFFFFFFFFFF, 0, 0, IntPtr.Zero);
            EnableTraceEx2(_sessionHandle, ref dxg, 1, 4, 0x0000000008000001UL, 0, 0, IntPtr.Zero); // Base + Present (0x8000000)

            _callbackKeepAlive = OnEventRecord;
            var log = new EVENT_TRACE_LOGFILEW
            {
                LoggerName = SessionName,
                ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callbackKeepAlive)
            };

            _traceHandle = OpenTrace(ref log);
            if (_traceHandle == INVALID_PROCESSTRACE_HANDLE)
            {
                int err = Marshal.GetLastWin32Error();
                LoggerService.Info($"ETW FPS disabled: OpenTrace failed win32={err}");
                StopSession();
                IsAvailable = false;
                return;
            }

            IsAvailable = true;
            _worker = new Thread(() =>
            {
                try
                {
                    ProcessTrace(new[] { _traceHandle }, 1, IntPtr.Zero, IntPtr.Zero);
                }
                catch { }
            })
            {
                IsBackground = true,
                Name = "DesktopIsland_ETW_FPS"
            };
            _worker.Start();
            LoggerService.Info("ETW DXGI/DxgKrnl real-time FPS monitor active.");
        }
        finally
        {
            Marshal.FreeHGlobal(propsMem);
        }
    }

    private void OnEventRecord(ref EVENT_RECORD rec)
    {
        // DXGI Task 9 = Present, Task 14 = PresentMultiplaneOverlay (Opcode 1 = Start)
        if (rec.ProviderId == DxgiProvider && (rec.Task == 9 || rec.Task == 14) && rec.Opcode == 1)
        {
            RecordFrame(_dxgiFramesByPid, rec.ProcessId);
        }
        // DxgKrnl Task 107 = Present, Task 3 = Flip, Task 142/143 = MPO (works for OpenGL / Vulkan / DX9 too)
        else if (rec.ProviderId == DxgKrnlProvider && (rec.Task == 107 || rec.Task == 3 || rec.Task == 142) && (rec.Opcode == 1 || rec.Opcode == 0))
        {
            RecordFrame(_dxgFramesByPid, rec.ProcessId);
        }
    }

    private static void RecordFrame(ConcurrentDictionary<uint, Queue<long>> map, uint pid)
    {
        long now = Environment.TickCount64;
        var q = map.GetOrAdd(pid, _ => new Queue<long>());
        lock (q)
        {
            q.Enqueue(now);
            while (q.Count > 0 && now - q.Peek() > 1500)
                q.Dequeue();
        }
    }

    public double? GetPidFps(uint pid)
    {
        if (!IsAvailable || pid == 0) return null;
        var dxgiFps = CountRecent(_dxgiFramesByPid, pid);
        if (dxgiFps.HasValue && dxgiFps.Value > 0) return dxgiFps;
        return CountRecent(_dxgFramesByPid, pid);
    }

    private static double? CountRecent(ConcurrentDictionary<uint, Queue<long>> map, uint pid)
    {
        if (!map.TryGetValue(pid, out var q)) return null;
        long now = Environment.TickCount64;
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() > 1000)
                q.Dequeue();
            return q.Count > 0 ? q.Count : null;
        }
    }

    private static IntPtr AllocTraceProperties()
    {
        int sz = 2048;
        IntPtr mem = Marshal.AllocHGlobal(sz);
        for (int i = 0; i < sz; i++) Marshal.WriteByte(mem, i, 0);
        Marshal.WriteInt32(mem, 0, sz);
        Marshal.WriteInt32(mem, 116, 120);
        return mem;
    }

    private void StopSession()
    {
        lock (_sync)
        {
            if (_traceHandle != INVALID_PROCESSTRACE_HANDLE)
            {
                try { CloseTrace(_traceHandle); } catch { }
                _traceHandle = INVALID_PROCESSTRACE_HANDLE;
            }
            if (_sessionHandle != 0)
            {
                IntPtr p = AllocTraceProperties();
                try { ControlTrace(_sessionHandle, SessionName, p, EVENT_TRACE_CONTROL_STOP); } catch { }
                finally { Marshal.FreeHGlobal(p); }
                _sessionHandle = 0;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopSession();
    }
}
