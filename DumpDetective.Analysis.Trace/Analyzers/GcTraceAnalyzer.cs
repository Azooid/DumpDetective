using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using static DumpDetective.Core.Tracing.TraceEventKind;


namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses GC events from a .nettrace / .etl trace file.
/// Extracts per-GC pause times, generation, trigger reason, heap sizes before/after.
/// </summary>
public sealed class GcTraceAnalyzer
{
    public GcTraceData Analyze(string tracePath, int top = 30, string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new GcTraceData($"Failed: {ex.Message}", processFilter, 0, 0, 0, 0, [], [], []);
        }
    }

    private static readonly Dictionary<string, byte> EvKind = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly Dictionary<int, PendingGc> Pending = new();
        internal readonly List<GcEvent> Complete = new();
        internal long LastHeapTotal;

        public void Consume(TraceEvent ev, in TraceEventMeta meta, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            if (!EvKind.TryGetValue(meta.EventName, out byte kind))
                EvKind[meta.EventName] = kind = ClassifyGcEvent(meta.EventName);
            if (kind == 0) return;

            if (kind == 1)
            {
                int gcIdx = SafeInt(ev, "Count");
                int gen   = SafeInt(ev, "Depth");
                string reason = SafeStr(ev, "Reason");
                string type   = SafeStr(ev, "Type");
                Pending[gcIdx] = new PendingGc(gcIdx, gen, reason, type, timestampMs, LastHeapTotal);
                return;
            }

            if (kind == 2)
            {
                long heapTotal = SafeLong(ev, "GenerationSize0") +
                                 SafeLong(ev, "GenerationSize1") +
                                 SafeLong(ev, "GenerationSize2") +
                                 SafeLong(ev, "GenerationSize3");
                if (heapTotal > 0)
                {
                    LastHeapTotal = heapTotal;
                    if (Complete.Count > 0)
                    {
                        var last = Complete[^1];
                        if (last.HeapSizeAfter == 0)
                            Complete[^1] = last with { HeapSizeAfter = heapTotal };
                    }
                }
                return;
            }

            // kind == 3: GC/Stop
            {
                int gcIdx = SafeInt(ev, "Count");
                if (Pending.TryGetValue(gcIdx, out var p))
                {
                    double pauseMs = timestampMs - p.StartMs;
                    Complete.Add(new GcEvent(
                        p.GcIndex, p.Gen, NormalizeReason(p.Reason), NormalizeType(p.Type),
                        pauseMs,
                        p.HeapBefore, 0,
                        p.StartMs));
                    Pending.Remove(gcIdx);
                }
            }
        }

        public bool WantsEvent(in TraceEventMeta meta)
        {
            if (!EvKind.TryGetValue(meta.EventName, out byte v))
                EvKind[meta.EventName] = v = meta.Kind switch
                {
                    _ when meta.Kind == GCStart => 1,
                    _ when meta.Kind == GCHeapStats => 2,
                    _ when meta.Kind == GCStop => 3,
                    _ when meta.IsKnown => 0,
                    _ => ClassifyGcEvent(meta.EventName)
                };
            return v != 0;
        }

        public void OnComplete() { }

        private static byte ClassifyGcEvent(string n) =>
            n.EndsWith("GC/Start",    StringComparison.OrdinalIgnoreCase) || n.EndsWith("GCStart",    StringComparison.OrdinalIgnoreCase) ? (byte)1 :
            n.EndsWith("GCHeapStats", StringComparison.OrdinalIgnoreCase) || n.EndsWith("GC/HeapStats",StringComparison.OrdinalIgnoreCase) ? (byte)2 :
            n.EndsWith("GC/Stop",     StringComparison.OrdinalIgnoreCase) || n.EndsWith("GCStop",     StringComparison.OrdinalIgnoreCase) ? (byte)3 :
            (byte)0;
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null)
        => new Consumer(processFilter);

    public GcTraceData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 30,
                                    string? processFilter = null)
    {
        var c = (Consumer)consumer;
        if (c.Complete.Count == 0)
            return new GcTraceData(
                $"{traceFileName}  |  0 GC events found — collect with GC events enabled",
                processFilter, 0, 0, 0, 0, [], [], []);

        double totalPause = c.Complete.Sum(e => e.PauseMs);
        double maxPause   = c.Complete.Max(e => e.PauseMs);
        double avgPause   = totalPause / c.Complete.Count;

        var topPauses = c.Complete
            .OrderByDescending(e => e.PauseMs)
            .Take(top)
            .Select(e => new GcPauseEntry(e.GcIndex, e.Generation, e.Reason, e.Type,
                                          e.PauseMs, e.HeapSizeBefore, e.HeapSizeAfter))
            .ToList();

        var genSummary = c.Complete
            .GroupBy(e => e.Generation)
            .OrderBy(g => g.Key)
            .Select(g => new GcGenSummary(
                g.Key, g.Count(),
                g.Sum(e => e.PauseMs),
                g.Max(e => e.PauseMs),
                g.Average(e => e.PauseMs)))
            .ToList();

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {c.Complete.Count} GCs";

        return new GcTraceData(info, processFilter,
            c.Complete.Count, totalPause, maxPause, avgPause,
            topPauses, genSummary,
            c.Complete.OrderBy(e => e.TimeMs).ToList());
    }

    public GcTraceData Analyze(TraceLog trace, string traceFileName, int top = 30,
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
            return new GcTraceData($"Failed: {ex.Message}", processFilter, 0, 0, 0, 0, [], [], []);
        }
    }


    private static string NormalizeReason(string r) => r switch
    {
        "0" or "AllocSmall"      => "AllocSmall",
        "1" or "Induced"         => "Induced",
        "2" or "LowMemory"       => "LowMemory",
        "3" or "Empty"           => "Empty",
        "4" or "AllocLarge"      => "AllocLarge",
        "5" or "OutOfSpaceSOH"   => "OutOfSpaceSOH",
        "6" or "OutOfSpaceLOH"   => "OutOfSpaceLOH",
        "7" or "InducedNotForced"=> "InducedNotForced",
        _                        => r.Length > 0 ? r : "Unknown"
    };

    private static string NormalizeType(string t) => t switch
    {
        "0" or "NonConcurrent"   => "Blocking",
        "1" or "Background"      => "Background",
        "2" or "ForegroundInduced" => "ForegroundInduced",
        _                        => t.Length > 0 ? t : "Unknown"
    };

    private sealed class PendingGc(int gcIndex, int gen, string reason, string type, double startMs, long heapBefore)
    {
        public readonly int    GcIndex    = gcIndex;
        public readonly int    Gen        = gen;
        public readonly string Reason     = reason;
        public readonly string Type       = type;
        public readonly double StartMs    = startMs;
        public readonly long   HeapBefore = heapBefore;
    }
}
