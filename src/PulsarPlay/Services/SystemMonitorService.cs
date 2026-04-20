using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using PulsarPlay.Models;

namespace PulsarPlay.Services;

public class SystemMonitorService : IDisposable
{
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _ramCounter;
    private PerformanceCounter? _diskReadCounter;
    private PerformanceCounter? _diskWriteCounter;
    private bool _disposed;

    public void Initialize()
    {
        try { _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total"); _cpuCounter.NextValue(); } catch { }
        try { _ramCounter = new PerformanceCounter("Memory", "% Committed Bytes In Use"); _ramCounter.NextValue(); } catch { }
        try { _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total"); _diskReadCounter.NextValue(); } catch { }
        try { _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total"); _diskWriteCounter.NextValue(); } catch { }
    }

    public double GetCpuUsage() => _cpuCounter?.NextValue() ?? 0;

    public (double TotalGB, double UsedGB, double UsedPercent) GetMemoryInfo()
    {
        try
        {
            var memStatus = new MEMORYSTATUSEX();
            memStatus.dwLength = (uint)Marshal.SizeOf(memStatus);
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                var totalGB = memStatus.ullTotalPhys / (1024.0 * 1024 * 1024);
                var availableGB = memStatus.ullAvailPhys / (1024.0 * 1024 * 1024);
                var usedGB = totalGB - availableGB;
                var usedPercent = (usedGB / totalGB) * 100;
                return (totalGB, usedGB, usedPercent);
            }
            return (0, 0, 0);
        }
        catch { return (0, 0, 0); }
    }

    public List<ProcessInfo> GetTopProcesses(int count = 5)
    {
        var processes = new List<ProcessInfo>();
        try
        {
            var totalMem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            var allProcs = Process.GetProcesses();
            foreach (var proc in allProcs)
            {
                try
                {
                    if (proc.WorkingSet64 > 10 * 1024 * 1024)
                    {
                        processes.Add(new ProcessInfo { Name = proc.ProcessName, Percent = (proc.WorkingSet64 / (double)totalMem) * 100 });
                    }
                }
                catch { }
            }
        }
        catch { }
        return processes.OrderByDescending(p => p.Percent).Take(count).ToList();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _cpuCounter?.Dispose();
        _ramCounter?.Dispose();
        _diskReadCounter?.Dispose();
        _diskWriteCounter?.Dispose();
        _disposed = true;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
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
}