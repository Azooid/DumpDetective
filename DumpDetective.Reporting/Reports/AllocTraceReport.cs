using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class AllocTraceReport
{
    /// <param name="dumpTopTypes">
    /// Optional: top types from a memory dump taken during the same incident.
    /// When provided, a cross-reference column shows actual live heap bytes alongside
    /// the trace's sampled estimate, giving accurate size data for types that survive GC.
    /// </param>
    /// <param name="dumpTotalHeapBytes">Total heap bytes from the dump (for heap % column). 0 if unavailable.</param>
    public void Render(AllocTraceData data, IRenderSink sink, int top = 20,
        IReadOnlyList<TypeStat>? dumpTopTypes = null, long dumpTotalHeapBytes = 0)
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
            ("Dump cross-ref",    dumpTopTypes is not null ? $"{dumpTopTypes.Count} types from dump" : "(none — pass --dump for heap accuracy)"),
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

        // Build dump lookup: TypeName → live heap bytes
        Dictionary<string, long>? dumpBytes = null;
        if (dumpTopTypes is { Count: > 0 })
        {
            dumpBytes = new Dictionary<string, long>(dumpTopTypes.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var ts in dumpTopTypes)
                dumpBytes[ts.Name] = ts.TotalBytes;
        }

        bool hasDump = dumpBytes is not null;
        string[] typeHeaders = hasDump
            ? ["Type", "Estimated (trace)", "Live bytes (dump)", "Accuracy", "% alloc", "Ticks"]
            : ["Type", "Estimated bytes", "% of total", "Ticks"];

        var typeRows = new List<string[]>(Math.Min(top, data.TopTypes.Count));
        foreach (var t in data.TopTypes.Take(top))
        {
            if (hasDump)
            {
                bool found = dumpBytes!.TryGetValue(t.TypeName, out long liveBytes);
                string liveLabel = found ? DumpHelpers.FormatSize(liveBytes) : "—";
                string accuracy  = "";
                if (found && t.EstimatedBytes > 0)
                {
                    double ratio = liveBytes * 100.0 / t.EstimatedBytes;
                    // ratio > 100%  → more lives on heap than was estimated allocated (long-lived accumulation)
                    // ratio < 10%   → most allocated was short-lived and already collected
                    accuracy = ratio > 200 ? $"↑ {ratio:F0}% (accumulating)" :
                               ratio > 100 ? $"↑ {ratio:F0}% (some retained)" :
                               ratio > 20  ? $"{ratio:F0}% live"  :
                               ratio >= 1  ? $"↓ {ratio:F0}% (mostly short-lived)" :
                                             $"↓ <1% (mostly short-lived)";
                }
                typeRows.Add([t.TypeName, DumpHelpers.FormatSize(t.EstimatedBytes), liveLabel, accuracy, $"{t.PctOfTotal:F1}%", t.Ticks.ToString("N0")]);
            }
            else
            {
                typeRows.Add([t.TypeName, DumpHelpers.FormatSize(t.EstimatedBytes), $"{t.PctOfTotal:F1}%", t.Ticks.ToString("N0")]);
            }
        }
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
            typeHeaders,
            typeRows,
            $"Top {typeRows.Count} types by estimated allocation  |  " +
            $"~{DumpHelpers.FormatSize(data.EstimatedTotalBytes)} total" +
            (hasDump ? "  |  Live bytes from dump" : ""));

        // Dump-only cross-ref: types in dump that don't appear in the trace top list
        // These are long-surviving types that weren't hot in the allocation window.
        if (dumpBytes is not null && dumpTopTypes is not null)
        {
            var traceTypeNames = new HashSet<string>(data.TopTypes.Select(t => t.TypeName), StringComparer.OrdinalIgnoreCase);
            var dumpOnly = dumpTopTypes
                .Where(ts => !traceTypeNames.Contains(ts.Name) && ts.TotalBytes > 1024 * 1024)
                .Take(10)
                .ToList();
            if (dumpOnly.Count > 0)
            {
                sink.Section("Heap-Dominant Types Not in Trace Top (long-lived survivors)", "alloc-dump-only");
                sink.Alert(AlertLevel.Info,
                    "These types dominate the dump heap but did not appear in the trace's top allocators — " +
                    "meaning they were allocated before the trace window and are surviving across GC collections.",
                    "Run 'gc-roots' and 'memory-leak' on the dump to understand why they are being retained.");
                var dumpOnlyRows = dumpOnly.Select(ts => new[]
                {
                    ts.Name,
                    DumpHelpers.FormatSize(ts.TotalBytes),
                    ts.Count.ToString("N0"),
                    dumpTotalHeapBytes > 0 ? $"{ts.TotalBytes * 100.0 / dumpTotalHeapBytes:F1}%" : "—",
                }).ToList();
                sink.Table(
                    ["Type", "Live bytes (dump)", "Instance count", "% of heap"],
                    dumpOnlyRows,
                    "Types present in dump heap but absent from trace allocation hot-path");
            }
        }

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
