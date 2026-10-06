using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.Win32;
using SystemWidget.WidBar.ExtensionApp.Models;
using SystemWidget.WidBar.ExtensionApp.Services;

namespace SystemWidget.WidBar.ExtensionApp;

/// <summary>Local collector used by the widget itself. Never sends data anywhere.</summary>
internal sealed class LocalTelemetryCollector
{
    private FILETIME _idle, _kernel, _user;
    private bool _hasBaseline;
    private List<PerformanceCounter>? _gpuCounters;
    private List<PerformanceCounter>? _vramCounters;
    private int _emptyGpuSamples;
    // Claude usage is refreshed in the background once every 180 seconds and the latest value is read synchronously.
    // Sample() runs every second, so calling the HTTP API directly here would hit 429s.
    private readonly ClaudeUsageTracker _claudeTracker = ClaudeUsageTracker.Shared;
    // DXGI was verified on real hardware to return 15.8 GB of dedicated VRAM for an RX 9070 XT.
    // GPU utilisation collection continues even if this can't be read.
    private readonly double _vramTotalGb = ReadDxgiVramBytes() / 1073741824d;

    public MainPlugin.Snapshot Sample(bool includeGpu, bool includeClaude)
    {
        var memory = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref memory);
        double? cpu = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            if (_hasBaseline)
            {
                var idleDelta = AsUInt64(idle) - AsUInt64(_idle);
                var totalDelta = AsUInt64(kernel) - AsUInt64(_kernel) + AsUInt64(user) - AsUInt64(_user);
                if (totalDelta > 0) cpu = Math.Clamp(100d * (totalDelta - idleDelta) / totalDelta, 0, 100);
            }
            _idle = idle; _kernel = kernel; _user = user; _hasBaseline = true;
        }
        var totalGb = memory.ullTotalPhys / 1073741824d;
        return new MainPlugin.Snapshot
        {
            UpdatedAt = DateTimeOffset.Now,
            System = new MainPlugin.SystemStatus
            {
                CpuPercent = cpu,
                MemoryPercent = memory.dwMemoryLoad,
                MemoryUsedGb = (memory.ullTotalPhys - memory.ullAvailPhys) / 1073741824d,
                MemoryTotalGb = totalGb,
            },
            Gpu = includeGpu ? ReadGpu() : null,
            Claude = includeClaude ? ReadClaude() : null,
        };
    }

    private MainPlugin.ClaudeStatus? ReadClaude()
    {
        // Users with no credentials at all (e.g. not logged in to Claude Code) see "--".
        if (!_claudeTracker.CredentialsExist) return null;

        // Refreshes the 180-second cache in the background. Latest is null on the first call, so "--".
        _claudeTracker.EnsureFresh();
        if (_claudeTracker.Latest is not { } fetch) return null;
        var usage = fetch.Data;

        return new MainPlugin.ClaudeStatus
        {
            SessionPercent = usage.FiveHour?.Utilization,
            WeekPercent = usage.SevenDay?.Utilization,
            SessionSecondsRemaining = RemainingSeconds(usage.FiveHour?.ResetsAt),
            WeekSecondsRemaining = RemainingSeconds(usage.SevenDay?.ResetsAt),
            SessionResetsAt = usage.FiveHour?.ResetsAt,
            WeekResetsAt = usage.SevenDay?.ResetsAt,
            FetchedAt = fetch.FetchedAt,
        };
    }

    private static int? RemainingSeconds(DateTimeOffset? resetsAt)
    {
        if (resetsAt is null) return null;
        var remaining = (resetsAt.Value - DateTimeOffset.UtcNow).TotalSeconds;
        return Math.Max(0, (int)remaining);
    }

    private MainPlugin.GpuStatus? ReadGpu()
    {
        _gpuCounters ??= CreateCounters("GPU Engine", "Utilization Percentage");
        _vramCounters ??= CreateCounters("GPU Adapter Memory", "Dedicated Usage");
        var util = ReadCounterSum(_gpuCounters, out var utilHits);
        var bytes = ReadCounterSum(_vramCounters, out var memoryHits);
        if (utilHits == 0 && memoryHits == 0)
        {
            // Don't hold on to counters for exited processes. Re-enumerate after 3 consecutive empty samples.
            if (++_emptyGpuSamples >= 3)
            {
                DisposeCounters(_gpuCounters); DisposeCounters(_vramCounters);
                _gpuCounters = null; _vramCounters = null; _emptyGpuSamples = 0;
            }
            return null;
        }
        _emptyGpuSamples = 0;
        var usedGb = bytes / 1073741824d;
        return new MainPlugin.GpuStatus
        {
            UtilPercent = utilHits > 0 ? Math.Min(100d, util) : null,
            VramUsedGb = memoryHits > 0 ? usedGb : null,
            VramTotalGb = memoryHits > 0 && _vramTotalGb > 0 ? _vramTotalGb : null,
            VramPercent = memoryHits > 0 && _vramTotalGb > 0 ? Math.Clamp(usedGb * 100d / _vramTotalGb, 0, 100) : null,
        };
    }

    private static ulong ReadDxgiVramBytes()
    {
        ulong best = 0;
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out var factory) < 0) return 0;
            try
            {
                for (uint i = 0; ; i++)
                {
                    var enumAdapters = GetDelegate<EnumAdapters1>(factory, 12);
                    if (enumAdapters(factory, i, out var adapter) < 0) break;
                    // IDXGIAdapter1::GetDesc1 is vtable slot 11. Slot 10 is CheckInterfaceSupport.
                    try { var getDesc = GetDelegate<GetDesc1>(adapter, 11); if (getDesc(adapter, out var desc) >= 0) best = Math.Max(best, desc.DedicatedVideoMemory); }
                    finally { Marshal.Release(adapter); }
                }
            }
            finally { Marshal.Release(factory); }
        }
        catch { return 0; }
        return best;
    }

    private static T GetDelegate<T>(IntPtr com, int index) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(com), index * IntPtr.Size));
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IDXGIFactory1 { }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapters1(IntPtr self, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDesc1(IntPtr self, out DXGI_ADAPTER_DESC1 desc);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DXGI_ADAPTER_DESC1 { [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description; public uint VendorId, DeviceId, SubSysId, Revision; public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory; public long AdapterLuid; public uint Flags; }
    [DllImport("dxgi.dll", CallingConvention = CallingConvention.StdCall)] private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    private static List<PerformanceCounter> CreateCounters(string category, string counter)
    {
        var result = new List<PerformanceCounter>();
        try
        {
            foreach (var instance in new PerformanceCounterCategory(category).GetInstanceNames())
                try { result.Add(new PerformanceCounter(category, counter, instance, readOnly: true)); }
                catch (Exception) { }
        }
        catch (Exception) { }
        return result;
    }

    private static double ReadCounterSum(List<PerformanceCounter> counters, out int hits)
    {
        double total = 0; hits = 0;
        foreach (var counter in counters)
        {
            try { total += Math.Max(0, counter.NextValue()); hits++; }
            catch (Exception) { }
        }
        return total;
    }

    private static void DisposeCounters(List<PerformanceCounter>? counters)
    {
        if (counters is null) return;
        foreach (var counter in counters) counter.Dispose();
    }

    private static ulong AsUInt64(FILETIME value) => ((ulong)value.dwHighDateTime << 32) | value.dwLowDateTime;
    [StructLayout(LayoutKind.Sequential)] private struct FILETIME { public uint dwLowDateTime, dwHighDateTime; }
    [StructLayout(LayoutKind.Sequential)] private struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
}
