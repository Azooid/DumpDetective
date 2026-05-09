using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class JsonSerializationTraceReport
{
    public void Render(JsonSerializationTraceData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "JSON serialization cost analysis — measures allocation pressure and CPU time attributable to System.Text.Json, Newtonsoft.Json, and DataContract JSON code paths.",
            why: "JSON serialization is a common hot path in .NET web services. Repeated large serializations allocate Utf8JsonWriter buffers, JsonDocument trees, and string intermediates that drive GC pressure. CPU-heavy JSON work can saturate thread-pool threads and inflate request latency.",
            impact: "Heavy JSON allocation inflates Gen 0/1 GC frequency. Synchronous JSON serialization on the hot path holds ThreadPool threads for the full duration, contributing to starvation under load.",
            bullets: [
                "CPU % (inclusive) — fraction of CPU samples where any JSON frame was on the stack",
                "JSON alloc — estimated bytes allocated by JSON-library types (sampled every ~100 KB)",
                "Top callers — your code that calls into JSON libraries most frequently",
                "By library — breakdown across System.Text.Json, Newtonsoft.Json, DataContract JSON"
            ],
            action: "Pool or reuse Utf8JsonWriter / MemoryStream buffers with ArrayPool. " +
                    "Prefer streaming serialization (Utf8JsonWriter directly) over Document-based deserialization for large payloads. " +
                    "Use System.Text.Json source-gen (JsonSerializerContext) to eliminate reflection overhead. " +
                    "Cache repeated serializations of static or rarely-changing objects."
        );

        sink.Section("Trace Summary", "json-summary");
        double jsonAllocPct = data.EstimatedTotalAllocBytes > 0
            ? data.EstimatedJsonAllocBytes * 100.0 / data.EstimatedTotalAllocBytes
            : 0;
        sink.KeyValues([
            ("Trace",              data.TraceInfo),
            ("JSON CPU (inclusive)", data.TotalCpuSamples > 0
                ? $"{data.JsonCpuPct:F1}%  ({data.JsonCpuSamples:N0} / {data.TotalCpuSamples:N0} samples)"
                : "—  (no CPU samples in trace)"),
            ("JSON alloc (est.)",   data.EstimatedJsonAllocBytes > 0
                ? $"~{DumpHelpers.FormatSize(data.EstimatedJsonAllocBytes)}  ({jsonAllocPct:F1}% of all alloc)"
                : "—  (no JSON alloc ticks in trace)"),
            ("Total alloc ticks",   data.TotalAllocTicks > 0 ? data.TotalAllocTicks.ToString("N0") : "—"),
            ("Process filter",      data.FilteredProcess ?? "(all processes)"),
        ]);

        if (!data.HasData)
        {
            sink.Alert(AlertLevel.Warning, "No allocation or CPU sample events found in trace.",
                "JSON serialization analysis requires both allocation ticks and CPU sample events.",
                CollectionGuidance());
            return;
        }

        // ── No JSON detected ───────────────────────────────────────────────────
        if (data.EstimatedJsonAllocBytes == 0 && data.JsonCpuSamples == 0)
        {
            sink.Alert(AlertLevel.Info,
                "No JSON serialization activity detected in this trace.",
                "System.Text.Json, Newtonsoft.Json, and DataContract JSON frames were not observed in CPU samples or allocation ticks. " +
                "Either the process does not use JSON serialization, or the trace was too short to capture it.",
                CollectionGuidance());
            return;
        }

        // ── CPU alerts ─────────────────────────────────────────────────────────
        if (data.JsonCpuPct >= 30)
            sink.Alert(AlertLevel.Critical,
                $"JSON serialization consumes {data.JsonCpuPct:F1}% of CPU time.",
                "More than 30% of CPU samples have a JSON frame on the stack. JSON serialization is a dominant cost in this profile.",
                "Switch to System.Text.Json source-gen to remove reflection. Pool buffers with ArrayPool. Consider caching serialized payloads.");
        else if (data.JsonCpuPct >= 15)
            sink.Alert(AlertLevel.Warning,
                $"JSON serialization consumes {data.JsonCpuPct:F1}% of CPU time.",
                $"{data.JsonCpuSamples:N0} of {data.TotalCpuSamples:N0} CPU samples include a JSON frame. " +
                "This is a significant contributor — optimization will have visible impact.",
                "Profile top callers below to identify the highest-frequency serialization sites.");
        else if (data.JsonCpuPct > 0)
            sink.Alert(AlertLevel.Info,
                $"JSON serialization accounts for {data.JsonCpuPct:F1}% of CPU samples.",
                "Within normal range — monitor if load increases.");

        // ── Alloc alert ────────────────────────────────────────────────────────
        if (jsonAllocPct >= 20)
            sink.Alert(AlertLevel.Warning,
                $"JSON-related types account for {jsonAllocPct:F1}% of all observed allocations.",
                $"~{DumpHelpers.FormatSize(data.EstimatedJsonAllocBytes)} estimated allocation from JSON library types. " +
                "High allocation rates increase GC frequency and can cause latency spikes.",
                "Pool Utf8JsonWriter / MemoryStream buffers. Use source-gen to avoid reflection-based intermediaries.");

        // ── By-library breakdown ───────────────────────────────────────────────
        if (data.ByLibrary.Count > 0)
        {
            sink.Section("By Library", "json-by-library");
            var libRows = data.ByLibrary.Select(l => new[]
            {
                l.Library,
                l.AllocBytes > 0 ? $"~{DumpHelpers.FormatSize(l.AllocBytes)}" : "—",
                l.AllocTicks.ToString("N0"),
                l.CpuSamples > 0 ? l.CpuSamples.ToString("N0") : "—",
                l.CpuPct > 0 ? $"{l.CpuPct:F1}%" : "—",
            }).ToList();
            sink.Table(
                ["Library", "Est. alloc", "Alloc ticks", "CPU samples", "CPU %"],
                libRows,
                "Cost breakdown per JSON library");

            // Donut chart of alloc cost by library
            var libAllocSegs = data.ByLibrary
                .Where(l => l.AllocBytes > 0)
                .Select(l => (Label: l.Library, Value: (double)l.AllocBytes))
                .ToList();
            if (libAllocSegs.Count > 1)
                sink.DonutChart(libAllocSegs, "JSON allocation by library",
                    $"~{DumpHelpers.FormatSize(data.EstimatedJsonAllocBytes)}\ntotal JSON");
        }

        // ── Top allocating JSON types ─────────────────────────────────────────
        if (data.TopAllocTypes.Count > 0)
        {
            sink.Section($"Top JSON-Allocating Types (top {Math.Min(data.TopAllocTypes.Count, top)})", "json-alloc-types");
            var typeRows = data.TopAllocTypes.Take(top).Select(t => new[]
            {
                t.TypeName,
                t.Library,
                $"~{DumpHelpers.FormatSize(t.EstimatedBytes)}",
                $"{t.PctOfJsonAlloc:F1}%",
                t.Ticks.ToString("N0"),
            }).ToList();

            var typeSegs = data.TopAllocTypes.Take(6)
                .Select(t =>
                {
                    string lbl = t.TypeName.Contains('.')
                        ? t.TypeName[(t.TypeName.LastIndexOf('.') + 1)..]
                        : t.TypeName;
                    return (Label: lbl, Value: (double)t.EstimatedBytes);
                })
                .ToList();
            if (typeSegs.Count > 0)
                sink.DonutChart(typeSegs, "JSON allocation by type (top 6)",
                    $"~{DumpHelpers.FormatSize(data.EstimatedJsonAllocBytes)}\ntotal JSON");

            sink.Table(
                ["Type", "Library", "Est. bytes", "% of JSON alloc", "Ticks"],
                typeRows,
                $"Top {typeRows.Count} JSON types by estimated allocation");
        }

        // ── Top callers ───────────────────────────────────────────────────────
        if (data.TopCallers.Count > 0)
        {
            sink.Section($"Top JSON Callers (top {Math.Min(data.TopCallers.Count, top)})", "json-callers");
            sink.Text("These are user-code frames that sit directly above JSON library frames in " +
                      "call stacks — the code in your application that triggers serialization work most frequently.");
            var callerRows = data.TopCallers.Take(top).Select(c => new[]
            {
                c.CallerFrame,
                c.Library,
                c.EstimatedAllocBytes > 0 ? $"~{DumpHelpers.FormatSize(c.EstimatedAllocBytes)}" : "—",
                c.AllocTicks > 0 ? c.AllocTicks.ToString("N0") : "—",
                c.CpuSamples > 0 ? c.CpuSamples.ToString("N0") : "—",
            }).ToList();

            // ── Two separate charts: CPU samples + alloc bytes ────────────────
            // A single combined chart is misleading when a caller dominates by CPU
            // but has minimal alloc (or vice-versa). Show both scales independently.
            var cpuSegs = data.TopCallers.Take(6)
                .Where(c => c.CpuSamples > 0)
                .Select(c =>
                {
                    string frame = c.CallerFrame;
                    int dot = frame.LastIndexOf('.');
                    string lbl = dot >= 0 && dot < frame.Length - 1
                        ? frame[(dot + 1)..].TrimEnd(')')
                        : frame;
                    if (lbl.Length > 40) lbl = lbl[..40] + "…";
                    return (Label: lbl, Value: (double)c.CpuSamples);
                })
                .ToList();
            if (cpuSegs.Count > 0)
                sink.StackedBar(cpuSegs, unit: " samples", caption: "Top callers by CPU samples spent in JSON");

            var allocSegs = data.TopCallers.Take(6)
                .Where(c => c.EstimatedAllocBytes > 0)
                .Select(c =>
                {
                    string frame = c.CallerFrame;
                    int dot = frame.LastIndexOf('.');
                    string lbl = dot >= 0 && dot < frame.Length - 1
                        ? frame[(dot + 1)..].TrimEnd(')')
                        : frame;
                    if (lbl.Length > 40) lbl = lbl[..40] + "…";
                    return (Label: lbl, Value: (double)c.EstimatedAllocBytes);
                })
                .ToList();
            if (allocSegs.Count > 0)
                sink.StackedBar(allocSegs, caption: "Top callers by estimated JSON allocation bytes", valueMode: "size");

            sink.Table(
                ["Caller frame", "Library", "Est. alloc", "Alloc ticks", "CPU samples"],
                callerRows,
                "User-code frames that call into JSON libraries — ordered by combined alloc + CPU cost");
        }

        // ── Collection guidance for missing signals ─────────────────────────────
        bool missingAlloc = data.TotalAllocTicks == 0;
        bool missingCpu   = data.TotalCpuSamples == 0;
        if (missingAlloc || missingCpu)
        {
            string missing = (missingAlloc && missingCpu) ? "allocation ticks and CPU samples"
                           : missingAlloc ? "allocation ticks"
                           : "CPU samples";
            sink.Alert(AlertLevel.Info,
                $"Partial data — {missing} not found in trace.",
                "Some signals are missing. Re-collect with the providers below for full JSON cost visibility.",
                CollectionGuidance());
        }
    }

    private static string CollectionGuidance() =>
        "dotnet-trace:\n" +
        "  # Allocation ticks (verbose GC):\n" +
        "  dotnet trace collect --providers 'Microsoft-Windows-DotNETRuntime:0x1:5' -p <pid>\n\n" +
        "  # CPU samples + allocation:\n" +
        "  dotnet trace collect --profile cpu-sampling \\\n" +
        "    --providers 'Microsoft-Windows-DotNETRuntime:0x1:5' -p <pid>\n\n" +
        "PerfView:\n" +
        "  PerfView.exe /ClrEvents:GC,Type,GCHeapAndTypeNames,Default /KernelEvents:Profile /NoGui collect";

}
