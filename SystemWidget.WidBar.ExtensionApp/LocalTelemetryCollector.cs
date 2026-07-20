using System.Runtime.InteropServices;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Win32;

namespace SystemWidget.WidBar.ExtensionApp;

/// <summary>WidBar 自身が使うローカル計測器。データを外部送信しない。</summary>
internal sealed class LocalTelemetryCollector
{
    private FILETIME _idle, _kernel, _user;
    private bool _hasBaseline;
    private List<PerformanceCounter>? _gpuCounters;
    private List<PerformanceCounter>? _vramCounters;
    private int _emptyGpuSamples;
    // DXGI は実機で RX 9070 XT の専用VRAM 15.8GB を返すことを単体検証済み。
    // 取得不能時も GPU 使用率の収集は継続する。
    private readonly double _vramTotalGb = ReadDxgiVramBytes() / 1073741824d;

    public MainPlugin.Snapshot Sample()
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
            Codex = ReadCodex(),
            Gpu = ReadGpu(),
            Claude = ReadClaude(),
        };
    }

    private static MainPlugin.ClaudeStatus? ReadClaude()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        if (!Directory.Exists(root)) return null;
        var now = DateTimeOffset.UtcNow;
        // config.json と同じく、週次境界は金曜 10:59（ローカル時刻）。
        var localNow = DateTimeOffset.Now;
        var daysSinceFriday = (7 + (int)localNow.DayOfWeek - (int)DayOfWeek.Friday) % 7;
        var localStart = localNow.Date.AddDays(-daysSinceFriday).AddHours(10).AddMinutes(59);
        var weeklyStart = new DateTimeOffset(localStart, TimeZoneInfo.Local.GetUtcOffset(localStart)).ToUniversalTime();
        if (weeklyStart > now) weeklyStart = weeklyStart.AddDays(-7);
        var events = new List<(DateTimeOffset time, long tokens)>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).Where(p => File.GetLastWriteTimeUtc(p) >= weeklyStart.AddHours(-5).UtcDateTime))
            foreach (var line in File.ReadLines(file))
            {
                if (!line.Contains("\"usage\"")) continue;
                using var doc = JsonDocument.Parse(line); var obj = doc.RootElement;
                if (!obj.TryGetProperty("timestamp", out var raw) || !DateTimeOffset.TryParse(raw.GetString(), out var time) || !obj.TryGetProperty("message", out var msg) || !msg.TryGetProperty("usage", out var usage)) continue;
                if (msg.TryGetProperty("model", out var model) && model.GetString() == "<synthetic>") continue;
                var tokens = new[] { "input_tokens", "output_tokens", "cache_creation_input_tokens", "cache_read_input_tokens" }.Sum(k => usage.TryGetProperty(k, out var n) ? n.GetInt64() : 0);
                if (tokens > 0) events.Add((time, tokens));
            }
        }
        catch (Exception) { return null; }
        var weekTokens = events.Where(e => e.time >= weeklyStart).Sum(e => e.tokens);
        var active = events.Where(e => e.time >= now.AddHours(-5)).ToList();
        var sessionTokens = active.Sum(e => e.tokens);
        return new MainPlugin.ClaudeStatus { SessionPercent = active.Count == 0 ? null : Math.Min(100, sessionTokens * 100d / 230000000), WeekPercent = Math.Min(100, weekTokens * 100d / 4100000000), SessionSecondsRemaining = active.Count == 0 ? null : Math.Max(0, (int)(active.Max(e => e.time).AddHours(5) - now).TotalSeconds), WeekSecondsRemaining = Math.Max(0, (int)(weeklyStart.AddDays(7) - now.UtcDateTime).TotalSeconds) };
    }

    private MainPlugin.GpuStatus? ReadGpu()
    {
        _gpuCounters ??= CreateCounters("GPU Engine", "Utilization Percentage");
        _vramCounters ??= CreateCounters("GPU Adapter Memory", "Dedicated Usage");
        var util = ReadCounterSum(_gpuCounters, out var utilHits);
        var bytes = ReadCounterSum(_vramCounters, out var memoryHits);
        if (utilHits == 0 && memoryHits == 0)
        {
            // 終了済みプロセスのカウンターを抱え続けない。3回連続だけ再列挙する。
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
                    // IDXGIAdapter1::GetDesc1 は vtable の 11 番目。10 は CheckInterfaceSupport。
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

    private static MainPlugin.CodexStatus? ReadCodex()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
        if (!Directory.Exists(root)) return null;
        DateTimeOffset? newest = null;
        double? primary = null, secondary = null;
        long? primaryReset = null, secondaryReset = null;
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories)
                         .Where(p => File.GetLastWriteTimeUtc(p) >= DateTime.UtcNow.AddHours(-48)))
            {
                // Codex が書き込み中のファイルや壊れた過去ログがあっても、
                // 他セッションの正しい token_count を捨てない。
                try
                {
                    foreach (var line in File.ReadLines(path))
                    {
                        if (!line.Contains("\"rate_limits\"")) continue;
                        using var doc = JsonDocument.Parse(line);
                        var rootEl = doc.RootElement;
                        if (!rootEl.TryGetProperty("timestamp", out var time) || !DateTimeOffset.TryParse(time.GetString(), out var ts)) continue;
                        if (newest is { } previous && ts <= previous) continue;
                        if (!rootEl.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("type", out var type) || type.GetString() != "token_count" || !payload.TryGetProperty("rate_limits", out var limits)) continue;
                        newest = ts;
                        (primary, primaryReset) = ReadWindow(limits, "primary");
                        (secondary, secondaryReset) = ReadWindow(limits, "secondary");
                    }
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { return null; }
        if (newest is null) return null;
        return new MainPlugin.CodexStatus { ShortPercent = primary, WeekPercent = secondary, ShortSecondsRemaining = SecondsRemaining(primaryReset), WeekSecondsRemaining = SecondsRemaining(secondaryReset) };
    }

    private static (double? used, long? reset) ReadWindow(JsonElement limits, string name)
    {
        if (!limits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) return (null, null);
        return (window.TryGetProperty("used_percent", out var used) ? used.GetDouble() : null,
                window.TryGetProperty("resets_at", out var reset) ? reset.GetInt64() : null);
    }

    private static int? SecondsRemaining(long? epoch) => epoch is null ? null : Math.Max(0, (int)(DateTimeOffset.FromUnixTimeSeconds(epoch.Value) - DateTimeOffset.UtcNow).TotalSeconds);

    private static ulong AsUInt64(FILETIME value) => ((ulong)value.dwHighDateTime << 32) | value.dwLowDateTime;
    [StructLayout(LayoutKind.Sequential)] private struct FILETIME { public uint dwLowDateTime, dwHighDateTime; }
    [StructLayout(LayoutKind.Sequential)] private struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
}
