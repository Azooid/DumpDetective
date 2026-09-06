using DumpDetective.Analysis.WebTrace.Model;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.WebTrace.Analysis;

/// <summary>
/// Turns the counter series (<c>jsHeapSizeUsed</c> / DOM <c>nodes</c> / <c>jsEventListeners</c>)
/// from a Chrome trace into a leak verdict. Pure POCO — same "no engine types leak into the
/// scorer" shape as <c>HealthScorer</c> — so it's unit-testable against synthetic series
/// without a real trace file.
/// </summary>
public static class WebMemoryLeakAnalyzer
{
    // Listener:node ratio a healthy page never approaches — a rebind-without-cleanup pattern
    // routinely produces single-digit-to-double-digit ratios (see Docs/WebTrace-Plan.md).
    private const double ListenerRatioWarn     = 3.0;
    private const double ListenerRatioCritical = 6.0;

    // Net growth from first-tenth to last-tenth of the recording, as a fraction of the
    // starting value. GC/deallocation noise averages out because these are window averages,
    // not single first/last points.
    private const double GrowthWarnFraction     = 0.5;
    private const double GrowthCriticalFraction = 1.5;

    public static WebMemoryLeakData Analyze(WebTraceData data, string traceFileName)
    {
        var samples = data.Counters;
        var findings = new List<Finding>();

        if (samples.Count == 0)
        {
            return new WebMemoryLeakData
            {
                TraceInfo        = traceFileName,
                HeapSeriesMb     = [],
                NodesSeries      = [],
                ListenerSeries   = [],
                Findings         = [new Finding(FindingSeverity.Info, "Summary",
                    "No UpdateCounters samples found in this trace.",
                    Advice: "Record with the DevTools Performance panel's memory checkbox enabled.")],
            };
        }

        var heapMb    = samples.Select(s => s.JsHeapSizeUsed / 1024.0 / 1024.0).ToList();
        var nodes     = samples.Select(s => (double)s.Nodes).ToList();
        var listeners = samples.Select(s => (double)s.JsEventListeners).ToList();

        long maxHeap      = samples.Max(s => s.JsHeapSizeUsed);
        int  maxNodes     = samples.Max(s => s.Nodes);
        int  maxListeners = samples.Max(s => s.JsEventListeners);
        double ratio      = maxNodes > 0 ? (double)maxListeners / maxNodes : 0;

        double heapGrowth      = WindowGrowthFraction(samples.Select(s => (double)s.JsHeapSizeUsed).ToList());
        double nodesGrowth     = WindowGrowthFraction(nodes);
        double listenerGrowth  = WindowGrowthFraction(listeners);

        // ── Listener leak ──────────────────────────────────────────────────────
        if (ratio >= ListenerRatioCritical)
        {
            findings.Add(new Finding(FindingSeverity.Critical, "Web Listeners",
                $"{maxListeners:N0} event listeners registered against only {maxNodes:N0} DOM nodes (~{ratio:F1}:1)",
                Detail: "A healthy page stays well under 1 listener per node. A ratio this high means listeners " +
                        "are being (re)bound without ever being removed — every render/update adds more instead of replacing.",
                Advice: "Search for addEventListener calls without a matching removeEventListener, especially inside " +
                        "loops, list/grid row renderers, or a component that re-subscribes on every update.",
                Deduction: 40));
        }
        else if (ratio >= ListenerRatioWarn)
        {
            findings.Add(new Finding(FindingSeverity.Warning, "Web Listeners",
                $"{maxListeners:N0} event listeners registered against {maxNodes:N0} DOM nodes (~{ratio:F1}:1)",
                Advice: "Elevated but not yet extreme — watch this ratio across a longer recording to confirm it keeps climbing.",
                Deduction: 15));
        }

        // ── Heap growth ─────────────────────────────────────────────────────────
        if (heapGrowth >= GrowthCriticalFraction)
        {
            findings.Add(new Finding(FindingSeverity.Critical, "Web Memory",
                $"JS heap grew {heapGrowth * 100:F0}% over the recording (peak {maxHeap / 1024.0 / 1024.0:F1} MB)",
                Advice: "Sustained growth across the whole recording (not just GC sawtooth) is the signature of a JS-side leak.",
                Deduction: 35));
        }
        else if (heapGrowth >= GrowthWarnFraction)
        {
            findings.Add(new Finding(FindingSeverity.Warning, "Web Memory",
                $"JS heap grew {heapGrowth * 100:F0}% over the recording (peak {maxHeap / 1024.0 / 1024.0:F1} MB)",
                Deduction: 15));
        }

        // ── DOM node growth ──────────────────────────────────────────────────────
        if (nodesGrowth >= GrowthCriticalFraction)
        {
            findings.Add(new Finding(FindingSeverity.Critical, "Web Memory",
                $"DOM node count grew {nodesGrowth * 100:F0}% over the recording (peak {maxNodes:N0})",
                Advice: "Nodes accumulating without ever being removed is a detached-DOM-tree leak — elements are " +
                        "removed from view but something (often the same stray listener) still references them.",
                Deduction: 25));
        }
        else if (nodesGrowth >= GrowthWarnFraction)
        {
            findings.Add(new Finding(FindingSeverity.Warning, "Web Memory",
                $"DOM node count grew {nodesGrowth * 100:F0}% over the recording (peak {maxNodes:N0})",
                Deduction: 10));
        }

        if (findings.Count == 0)
            findings.Add(new Finding(FindingSeverity.Info, "Summary",
                "No memory-growth or listener-leak signal detected in this recording."));

        return new WebMemoryLeakData
        {
            TraceInfo             = traceFileName,
            HeapSeriesMb          = heapMb,
            NodesSeries           = nodes,
            ListenerSeries        = listeners,
            MaxHeapBytes          = maxHeap,
            MaxNodes              = maxNodes,
            MaxListeners          = maxListeners,
            ListenerNodeRatio     = ratio,
            HeapGrowthFraction    = heapGrowth,
            NodesGrowthFraction   = nodesGrowth,
            ListenerGrowthFraction = listenerGrowth,
            Findings              = findings,
        };
    }

    /// <summary>
    /// (average of the last 10% of samples − average of the first 10%) / average of the first 10%.
    /// Window-averaged instead of endpoint-to-endpoint so a single GC dip/spike at either end
    /// doesn't flip the verdict.
    /// </summary>
    private static double WindowGrowthFraction(IReadOnlyList<double> series)
    {
        if (series.Count < 4) return 0;
        int window = Math.Max(1, series.Count / 10);
        double first = series.Take(window).Average();
        double last  = series.Skip(series.Count - window).Average();
        return first > 0 ? (last - first) / first : 0;
    }
}
