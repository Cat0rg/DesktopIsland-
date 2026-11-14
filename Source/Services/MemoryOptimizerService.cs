using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DesktopIsland.Models;

namespace DesktopIsland.Services;

public static class MemoryOptimizerService
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessSetQuota = 0x0100;
    private const int SystemMemoryListInformation = 80;
    private const int MemoryEmptyWorkingSets = 2;
    private const int MemoryPurgeLowPriorityStandbyList = 5;

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

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int systemInformationClass, ref int systemInformation, int systemInformationLength);

    public static Task<MemoryOptimizationResult> OptimizeAsync(uint? excludePid = null)
    {
        return Task.Run(async () =>
        {
            var before = SampleMemory();

            int trimmed = 0;
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            catch { }

            foreach (var proc in Process.GetProcesses())
            {
                IntPtr hProc = IntPtr.Zero;
                try
                {
                    if (proc.Id <= 4 || (excludePid.HasValue && proc.Id == (int)excludePid.Value))
                        continue;
                    hProc = OpenProcess(ProcessQueryInformation | ProcessSetQuota, false, proc.Id);
                    if (hProc != IntPtr.Zero && EmptyWorkingSet(hProc))
                    {
                        trimmed++;
                    }
                }
                catch { }
                finally
                {
                    if (hProc != IntPtr.Zero) CloseHandle(hProc);
                    proc.Dispose();
                }
            }

            // If running elevated, also ask Windows memory manager to trim system working sets & low-priority standby
            try
            {
                int cmd = MemoryEmptyWorkingSets;
                NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int));
                int standby = MemoryPurgeLowPriorityStandbyList;
                NtSetSystemInformation(SystemMemoryListInformation, ref standby, sizeof(int));
            }
            catch { }

            await Task.Delay(250);
            var after = SampleMemory();

            double freedGb = Math.Max(0, before.usedGb - after.usedGb);
            LoggerService.Info($"RAM optimization complete: trimmed={trimmed} processes, used {before.usedGb:F2} -> {after.usedGb:F2} GB (freed {freedGb:F2} GB)");

            return new MemoryOptimizationResult
            {
                BeforeUsedGb = before.usedGb,
                AfterUsedGb = after.usedGb,
                FreedGb = freedGb,
                BeforePercent = before.percent,
                AfterPercent = after.percent,
                TrimmedProcessesCount = trimmed
            };
        });
    }

    private static (double usedGb, uint percent) SampleMemory()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref mem)) return (0, 0);
        const double gb = 1024.0 * 1024.0 * 1024.0;
        return ((mem.ullTotalPhys - mem.ullAvailPhys) / gb, mem.dwMemoryLoad);
    }
}
