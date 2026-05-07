using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class AllocTraceReport
{
    public void Render(AllocTraceData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "Allocation hotspot analysis — GCAllocationTick events sampled every ~100 KB to identify the heaviest allocating types and call sites.",
            why: "Excessive allocations increase GC frequency and pause times. Short-lived large objects land on the LOH and trigger Gen 2 GCs. Pinpointing which types and call sites allocate most enables targeted pooling or lifetime improvements.",
            impact: "Allocation-heavy paths generate GC pressure even if objects are short-lived. The GC cost is paid regardless of whether objects survive.",
            bullets: [
                "Tick = GCAllocationTick event ≈ every 100 KB allocated (sampled, not exact)",
                "Estimated bytes are approximate — actual allocation may differ slightly",
                "Top call site → the innermost non-runtime frame that triggered the allocation"
            ],
            action: "Pool or reuse the top allocating types (ArrayPool<T>, MemoryPool, object pools). For strings, prefer interpolation with Span or StringBuilder for hot paths. Avoid boxing in tight loops."
        );

        sink.Section("Trace Summary", "alloc-summary");
        sink.KeyValues([
            ("Trace",             data.TraceInfo),
            ("Allocation ticks",  data.TotalTicks.ToString("N0")),
            ("Estimated total",   DumpHelpers.FormatSize(data.EstimatedTotalBytes)),
            ("Process filter",    data.FilteredProcess ?? "(all processes)"),
        ]);

        if (data.TotalTicks == 0)
        {
            sink.Alert(AlertLevel.Warning, "No GCAllocationTick events found in trace.",
                "To capture allocation events, re-collect with one of the following:",
                "dotnet-trace:\n" +
                "  dotnet-trace collect --profile gc-verbose\n\n" +
                "PerfView:\n" +
                "  PerfView.exe /ClrEvents:GC,Type,GCHeapAndTypeNames,Default /NoGui collect");
            return;
        }

        // Top types
        sink.Section("Top Allocating Types", "alloc-types");
        var typeRows = data.TopTypes.Take(top).Select(t => new[]
        {
            t.TypeName,
            DumpHelpers.FormatSize(t.EstimatedBytes),
            $"{t.PctOfTotal:F1}%",
            t.Ticks.ToString("N0"),
        }).ToList();
        // Allocation breakdown by type — donut
        var allocSegs = data.TopTypes.Take(8)
            .Select(t => {
                string lbl = t.TypeName.Contains('.')
                    ? t.TypeName[(t.TypeName.LastIndexOf('.') + 1)..]
                    : t.TypeName;
                return (Label: lbl, Value: (double)t.EstimatedBytes);
            })
            .ToList();
        if (allocSegs.Count > 0)
            sink.DonutChart(allocSegs, "Allocation breakdown by type (top 8)",
                $"~{DumpHelpers.FormatSize(data.EstimatedTotalBytes)}\ntotal");

        sink.Table(
            ["Type", "Estimated bytes", "% of total", "Ticks"],
            typeRows,
            $"Top {typeRows.Count} types by estimated allocation  |  " +
            $"~{DumpHelpers.FormatSize(data.EstimatedTotalBytes)} total");

        // Top call sites
        sink.Section("Top Allocating Call Sites", "alloc-callsites");
        var csRows = data.TopCallSites.Take(top).Select(cs => new[]
        {
            TrimFrame(cs.TopFrame, 80),
            cs.TypeName,
            DumpHelpers.FormatSize(cs.EstimatedBytes),
            cs.Ticks.ToString("N0"),
        }).ToList();

        // Call site allocation stacked bar — label by type name (unique) not call-site frame
        // (multiple call sites can share the same TopFrame when different types are allocated
        //  from the same method, producing duplicate labels if we use TopFrame).
        var csSegs = data.TopCallSites.Take(6)
            .Select(cs => {
                string lbl = TrimTypeName(cs.TypeName, 35);
                return (Label: lbl, Value: (double)cs.EstimatedBytes);
            })
            .ToList();
        if (csSegs.Count > 0)
            sink.StackedBar(csSegs, valueMode: "size", caption: "Estimated allocation by type (top 6 call sites)");

        sink.Table(
            ["Call site", "Type allocated", "Estimated bytes", "Ticks"],
            csRows,
            $"Top {csRows.Count} call sites by estimated allocation size");

        // Alert if any single type dominates
        if (data.TopTypes.Count > 0 && data.TopTypes[0].PctOfTotal > 40)
        {
            sink.Alert(AlertLevel.Warning,
                $"{data.TopTypes[0].TypeName} accounts for {data.TopTypes[0].PctOfTotal:F1}% of allocations.",
                $"Estimated {DumpHelpers.FormatSize(data.TopTypes[0].EstimatedBytes)} allocated.",
                "Consider pooling or reusing instances of this type.");
        }
    }

    private static string TrimFrame(string s, int max) =>
        TraceReportHelpers.TrimFrame(s, max);

    // Trim a fully-qualified type name: keep the last two segments (e.g. "Span.End" from
    // "Elastic.Apm.Model.Span.End") so the chart label stays short but still meaningful.
    private static string TrimTypeName(string s, int max)
    {
        if (s.Length <= max) return s;
        // Try to take the last two dot-separated tokens for readability
        int lastDot = s.LastIndexOf('.');
        if (lastDot > 0)
        {
            int prevDot = s.LastIndexOf('.', lastDot - 1);
            string shortened = prevDot >= 0 ? s[(prevDot + 1)..] : s[(lastDot + 1)..];
            return shortened.Length <= max ? shortened : "…" + shortened[^(max - 1)..];
        }
        return "…" + s[^(max - 1)..];
    }
}
