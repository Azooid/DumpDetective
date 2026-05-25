using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using static DumpDetective.Core.Tracing.TraceEventKind;


namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses GCAllocationTick events from a .nettrace / .etl trace.
/// Each tick fires approximately every 100 KB allocated — totals are sampled estimates.
/// Identifies the hottest allocating types and call sites.
/// </summary>
public sealed class AllocTraceAnalyzer
{
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

    private static readonly Dictionary<string, bool> EvKind = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly Dictionary<string, TypeAcc> ByType = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, CallSiteAcc> ByCallSite = new(StringComparer.Ordinal);
        internal int TotalTicks;

        public void Consume(TraceEvent ev, in TraceEventMeta meta, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            if (!EvKind.TryGetValue(meta.EventName, out bool isAlloc))
                EvKind[meta.EventName] = isAlloc =
                    meta.EventName.EndsWith("GCAllocationTick",  StringComparison.OrdinalIgnoreCase) ||
                    meta.EventName.EndsWith("GC/AllocationTick", StringComparison.OrdinalIgnoreCase) ||
                    meta.EventName.IndexOf("AllocationTick",     StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isAlloc) return;

            TotalTicks++;

            string typeName = SafeStr(ev, "TypeName");
            if (typeName.Length == 0) typeName = SafeStr(ev, "AllocationTypeName");
            if (typeName.Length == 0) typeName = "(unknown)";

            long allocBytes = SafeLong(ev, "AllocationAmount64");
            if (allocBytes <= 0) allocBytes = SafeLong(ev, "AllocationAmount");
            if (allocBytes <= 0) allocBytes = GcAllocTickSizeBytes;

            if (!ByType.TryGetValue(typeName, out var tAcc))
                ByType[typeName] = tAcc = new TypeAcc();
            tAcc.Ticks++;
            tAcc.Bytes += allocBytes;

            string frame = TopUserFrame(ev, typeName);
            string key   = $"{frame}|{typeName}";
            if (!ByCallSite.TryGetValue(key, out var csAcc))
                ByCallSite[key] = csAcc = new CallSiteAcc(frame, typeName);
            csAcc.Ticks++;
            csAcc.Bytes += allocBytes;
        }

        public bool WantsEvent(in TraceEventMeta meta)
        {
            if (!EvKind.TryGetValue(meta.EventName, out bool v))
                EvKind[meta.EventName] = v = meta.Kind switch
                {
                    _ when meta.Kind == GCAllocationTick => true,
                    _ when meta.IsKnown => false,
                    _ => meta.EventName.EndsWith("GCAllocationTick",    StringComparison.OrdinalIgnoreCase) ||
                         meta.EventName.EndsWith("GC/AllocationTick",   StringComparison.OrdinalIgnoreCase) ||
                         meta.EventName.IndexOf("AllocationTick", StringComparison.OrdinalIgnoreCase) >= 0
                };
            return v;
        }

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null)
        => new Consumer(processFilter);

    public AllocTraceData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 20,
                                       string? processFilter = null)
    {
        var c = (Consumer)consumer;
        if (c.TotalTicks == 0)
            return new AllocTraceData(
                $"{traceFileName}  |  0 allocation ticks — collect with --profile gc-verbose or --providers Microsoft-Windows-DotNETRuntime:0x1:5",
                processFilter, 0, 0, [], []);

        long estimatedTotal = c.ByType.Values.Sum(a => a.Bytes);

        var topTypes = c.ByType
            .OrderByDescending(kv => kv.Value.Bytes)
            .Take(top)
            .Select(kv => new AllocTypeSummary(
                kv.Key, kv.Value.Ticks, kv.Value.Bytes,
                estimatedTotal > 0 ? kv.Value.Bytes * 100.0 / estimatedTotal : 0))
            .ToList();

        var topCallSites = c.ByCallSite
            .OrderByDescending(kv => kv.Value.Bytes)
            .Take(top)
            .Select(kv => new AllocCallSiteSummary(kv.Value.Frame, kv.Value.TypeName,
                                                    kv.Value.Ticks, kv.Value.Bytes))
            .ToList();

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {c.TotalTicks:N0} alloc ticks  •  ~{FormatBytes(estimatedTotal)} estimated";

        return new AllocTraceData(info, processFilter, c.TotalTicks, estimatedTotal, topTypes, topCallSites);
    }

    public AllocTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                   string? processFilter = null, Action<string>? progress = null)
    {
        try
        {
            var c = CreateConsumer(processFilter);
            TraceEventDispatcher.Dispatch(trace, c,
                progress);
            return BuildResult(c, traceFileName, top, processFilter);
        }
        catch (Exception ex)
        {
            return new AllocTraceData($"Failed: {ex.Message}", processFilter, 0, 0, [], []);
        }
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

    private sealed class TypeAcc    { public int Ticks; public long Bytes; }
    private sealed class CallSiteAcc(string frame, string typeName)
    {
        public readonly string Frame    = frame;
        public readonly string TypeName = typeName;
        public int  Ticks;
        public long Bytes;
    }
}
