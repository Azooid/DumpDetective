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
                "Collect with allocation events enabled.",
                "dotnet-trace: use --profile gc-verbose\nPerfView: enable 'GCAllocationTick' provider keyword 0x1");
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
        s.Length <= max ? s : "…" + s[^(max - 1)..];
}
