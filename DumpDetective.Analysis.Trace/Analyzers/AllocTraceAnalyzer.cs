using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses GCAllocationTick events from a .nettrace / .etl / .etl.zip trace.
/// Each tick fires approximately every 100 KB allocated — totals are sampled estimates.
/// Identifies the hottest allocating types and call sites.
/// </summary>
public sealed class AllocTraceAnalyzer
{
    private const long TickSizeBytes = 100_000; // ~100 KB per tick

    public AllocTraceData Analyze(string tracePath, int top = 20, string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new AllocTraceData($"Failed: {ex.Message}", processFilter, 0, 0, [], []);
        }
    }

    public AllocTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                   string? processFilter = null)
    {
        var byType     = new Dictionary<string, TypeAcc>(StringComparer.Ordinal);
        var byCallSite = new Dictionary<string, CallSiteAcc>(StringComparer.Ordinal);
        int totalTicks = 0;

        foreach (var ev in trace.Events)
        {
            if (processFilter is not null &&
                !ev.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            string evName = ev.EventName ?? "";
            bool isAlloc =
                evName.EndsWith("GCAllocationTick",     StringComparison.OrdinalIgnoreCase) ||
                evName.EndsWith("GC/AllocationTick",    StringComparison.OrdinalIgnoreCase) ||
                evName.IndexOf("AllocationTick",        StringComparison.OrdinalIgnoreCase) >= 0;

            if (!isAlloc) continue;

            totalTicks++;

            string typeName = SafeStr(ev, "TypeName");
            if (typeName.Length == 0) typeName = SafeStr(ev, "AllocationTypeName");
            if (typeName.Length == 0) typeName = "(unknown)";

            long allocBytes = SafeLong(ev, "AllocationAmount64");
            if (allocBytes <= 0) allocBytes = SafeLong(ev, "AllocationAmount");
            if (allocBytes <= 0) allocBytes = TickSizeBytes;

            if (!byType.TryGetValue(typeName, out var tAcc))
                byType[typeName] = tAcc = new TypeAcc();
            tAcc.Ticks++;
            tAcc.Bytes += allocBytes;

            string frame = TopUserFrame(ev, typeName);
            string key   = $"{frame}|{typeName}";
            if (!byCallSite.TryGetValue(key, out var csAcc))
                byCallSite[key] = csAcc = new CallSiteAcc(frame, typeName);
            csAcc.Ticks++;
            csAcc.Bytes += allocBytes;
        }

        if (totalTicks == 0)
            return new AllocTraceData(
                $"{traceFileName}  |  0 allocation ticks — collect with --profile gc-verbose or --providers Microsoft-Windows-DotNETRuntime:0x1:5",
                processFilter, 0, 0, [], []);

        long estimatedTotal = byType.Values.Sum(a => a.Bytes);

        var topTypes = byType
            .OrderByDescending(kv => kv.Value.Bytes)
            .Take(top)
            .Select(kv => new AllocTypeSummary(
                kv.Key, kv.Value.Ticks, kv.Value.Bytes,
                estimatedTotal > 0 ? kv.Value.Bytes * 100.0 / estimatedTotal : 0))
            .ToList();

        var topCallSites = byCallSite
            .OrderByDescending(kv => kv.Value.Bytes)
            .Take(top)
            .Select(kv => new AllocCallSiteSummary(kv.Value.Frame, kv.Value.TypeName,
                                                    kv.Value.Ticks, kv.Value.Bytes))
            .ToList();

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {totalTicks:N0} alloc ticks  •  ~{FormatBytes(estimatedTotal)} estimated";

        return new AllocTraceData(info, processFilter, totalTicks, estimatedTotal, topTypes, topCallSites);
    }

    private static string SafeStr(TraceEvent ev, string field)
    {
        try { return ev.PayloadByName(field)?.ToString() ?? ""; } catch { return ""; }
    }

    private static long SafeLong(TraceEvent ev, string field)
    {
        try { return (long)Convert.ChangeType(ev.PayloadByName(field), typeof(long)); } catch { return 0; }
    }

    private static string TopUserFrame(TraceEvent ev, string typeName)
    {
        var cs = ev.CallStack();
        var cur = cs;
        while (cur is not null)
        {
            string? name = cur.CodeAddress.FullMethodName;
            if (!string.IsNullOrEmpty(name) &&
                !name.StartsWith("System.GC",                StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("System.Runtime.CompilerServices", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("clr!",                       StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("ntdll",                      StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("coreclr",                    StringComparison.OrdinalIgnoreCase))
                return name;
            cur = cur.Caller;
        }
        // Fall back to module name if no resolved user frame found
        var first = cs;
        while (first is not null)
        {
            string mod = first.CodeAddress.ModuleName ?? "";
            if (mod.Length > 0) return mod;
            first = first.Caller;
        }
        return "(no stack)";
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _           => $"{bytes} B"
    };

    private sealed class TypeAcc    { public int Ticks; public long Bytes; }
    private sealed class CallSiteAcc(string frame, string typeName)
    {
        public readonly string Frame    = frame;
        public readonly string TypeName = typeName;
        public int  Ticks;
        public long Bytes;
    }
}
