using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

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

    public GcTraceData Analyze(TraceLog trace, string traceFileName, int top = 30,
                                string? processFilter = null)
    {
        var pending  = new Dictionary<int, PendingGc>();
        var complete = new List<GcEvent>();
        long lastHeapTotal = 0;  // heap size from most recent GCHeapStats (pre-GC baseline)

        try
        {
            foreach (var ev in trace.Events)
            {
                if (processFilter is not null &&
                    !ev.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";

                // GC/Start — snapshot current heap total as HeapBefore for this GC
                if (evName.EndsWith("GC/Start", StringComparison.OrdinalIgnoreCase) ||
                    evName.EndsWith("GCStart", StringComparison.OrdinalIgnoreCase))
                {
                    int gcIdx = SafeInt(ev, "Count");
                    int gen   = SafeInt(ev, "Depth");
                    string reason = SafeStr(ev, "Reason");
                    string type   = SafeStr(ev, "Type");
                    pending[gcIdx] = new PendingGc(gcIdx, gen, reason, type, ev.TimeStampRelativeMSec, lastHeapTotal);
                    continue;
                }

                // GCHeapStats — fires AFTER GCStop; contains post-GC heap sizes.
                // Update lastHeapTotal for the next GC's HeapBefore, and back-fill
                // the most recently completed GC's HeapSizeAfter (which was set to 0).
                if (evName.EndsWith("GCHeapStats", StringComparison.OrdinalIgnoreCase) ||
                    evName.EndsWith("GC/HeapStats", StringComparison.OrdinalIgnoreCase))
                {
                    long heapTotal = SafeLong(ev, "GenerationSize0") +
                                     SafeLong(ev, "GenerationSize1") +
                                     SafeLong(ev, "GenerationSize2") +
                                     SafeLong(ev, "GenerationSize3");
                    if (heapTotal > 0)
                    {
                        lastHeapTotal = heapTotal;
                        // Back-fill HeapSizeAfter on the last completed GC entry
                        if (complete.Count > 0)
                        {
                            var last = complete[^1];
                            if (last.HeapSizeAfter == 0)
                                complete[^1] = last with { HeapSizeAfter = heapTotal };
                        }
                    }
                    continue;
                }

                // GC/Stop — heap sizes not available here; HeapAfter filled by GCHeapStats above
                if (evName.EndsWith("GC/Stop", StringComparison.OrdinalIgnoreCase) ||
                    evName.EndsWith("GCStop", StringComparison.OrdinalIgnoreCase))
                {
                    int gcIdx = SafeInt(ev, "Count");
                    if (pending.TryGetValue(gcIdx, out var p))
                    {
                        double pauseMs = ev.TimeStampRelativeMSec - p.StartMs;
                        complete.Add(new GcEvent(
                            p.GcIndex, p.Gen, NormalizeReason(p.Reason), NormalizeType(p.Type),
                            pauseMs,
                            p.HeapBefore, 0,   // HeapSizeAfter back-filled by next GCHeapStats
                            p.StartMs));
                        pending.Remove(gcIdx);
                    }
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            return new GcTraceData($"Failed: {ex.Message}", processFilter, 0, 0, 0, 0, [], [], []);
        }

        if (complete.Count == 0)
            return new GcTraceData(
                $"{traceFileName}  |  0 GC events found — collect with GC events enabled",
                processFilter, 0, 0, 0, 0, [], [], []);

        double totalPause = complete.Sum(e => e.PauseMs);
        double maxPause   = complete.Max(e => e.PauseMs);
        double avgPause   = totalPause / complete.Count;

        var topPauses = complete
            .OrderByDescending(e => e.PauseMs)
            .Take(top)
            .Select(e => new GcPauseEntry(e.GcIndex, e.Generation, e.Reason, e.Type,
                                          e.PauseMs, e.HeapSizeBefore, e.HeapSizeAfter))
            .ToList();

        var genSummary = complete
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
                      $"  |  {complete.Count} GCs";

        return new GcTraceData(info, processFilter,
            complete.Count, totalPause, maxPause, avgPause,
            topPauses, genSummary,
            complete.OrderBy(e => e.TimeMs).ToList());
    }

    private static int SafeInt(TraceEvent ev, string field)
    {
        try { return (int)Convert.ChangeType(ev.PayloadByName(field), typeof(int)); } catch { return 0; }
    }

    private static long SafeLong(TraceEvent ev, string field)
    {
        try { return (long)Convert.ChangeType(ev.PayloadByName(field), typeof(long)); } catch { return 0; }
    }

    private static string SafeStr(TraceEvent ev, string field)
    {
        try { return ev.PayloadByName(field)?.ToString() ?? ""; } catch { return ""; }
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
