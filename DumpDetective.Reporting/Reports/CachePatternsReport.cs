using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class CachePatternsReport
{
    public void Render(CachePatternsData data, IRenderSink sink, int unboundedThreshold = 10_000)
    {
        sink.Section("Cache & Collection Pattern Summary");
        sink.Explain(
            what: "Dictionary, ConcurrentDictionary, MemoryCache, and other collection types detected on the heap " +
                  "with their entry counts and total memory footprints.",
            why:  "Unbounded caches are one of the most common causes of slow memory leaks in .NET applications. " +
                  "A dictionary that grows without limit — because entries are never evicted — will gradually " +
                  "consume all available memory while never triggering a high-frequency allocation spike.",
            bullets:
            [
                $"Entry count > {unboundedThreshold:N0} → likely unbounded growth — add eviction policy or capacity limit",
                "ConcurrentDictionary with many instances → ensure each dictionary is bounded or collected",
                "MemoryCache without size limits → configure SizeLimit and register a removal callback",
                "Large HashSet → consider whether all entries are still needed or whether TTL-based eviction applies",
            ],
            action: "For each flagged type: add a capacity limit, use ConditionalWeakTable (weak-keyed cache), " +
                    "set IMemoryCache.SizeLimit, or use IDistributedCache with TTL.",
            impact: "An unbounded cache growing at 1 MB/min will exhaust 1 GB of RAM in ~17 hours — " +
                    "usually causing an OutOfMemoryException at an inconvenient time.");

        sink.KeyValues([
            ("Total instances",   data.TotalInstances.ToString("N0")),
            ("Total entries",     data.TotalEntryCount.ToString("N0")),
            ("Total own size",    DumpHelpers.FormatSize(data.TotalSize)),
            ("Total retained",    data.Entries.Any(e => e.RetainedSize > 0)
                                      ? DumpHelpers.FormatSize(data.Entries.Sum(e => e.RetainedSize))
                                      : "(BFS index not built)"),
            ("Unbounded flags",   data.Entries.Count(e => e.HasOversizedInstance).ToString("N0")),
        ]);

        int flagged = data.Entries.Count(e => e.HasOversizedInstance);
        if (flagged > 0)
            sink.Alert(AlertLevel.Warning,
                $"{flagged} collection type(s) have at least one instance with > {unboundedThreshold:N0} entries.",
                "These are the most likely unbounded cache candidates. Review whether eviction is configured.");

        if (data.Entries.Count == 0)
        {
            sink.Alert(AlertLevel.Info, "No cache-like collection types found on the heap.");
            return;
        }

        sink.Section("Collection Types by Entry Count");
        bool hasBfs = data.Entries.Any(e => e.RetainedSize > 0);
        var rows = data.Entries
            .OrderByDescending(e => e.TotalEntryCount)
            .Select(e =>
            {
                string sizeCol = hasBfs
                    ? DumpHelpers.FormatSize(e.RetainedSize) + (e.RetainedIsEstimated ? " ~" : "")
                    : DumpHelpers.FormatSize(e.TotalSize);
                return new[]
                {
                    e.TypeName.Length > 65 ? e.TypeName[..65] + "\u2026" : e.TypeName,
                    e.CollectionKind,
                    e.InstanceCount.ToString("N0"),
                    e.TotalEntryCount.ToString("N0"),
                    e.AverageEntries.ToString("N0"),
                    e.MaxEntries.ToString("N0"),
                    sizeCol,
                    e.HasOversizedInstance ? "\u26a0 Unbounded" : "\u2713 OK",
                };
            })
            .ToList();

        string sizeHeader = hasBfs ? "Retained Size" : "Own Size";
        string sizeNote   = hasBfs
            ? $"Retained Size = full object graph held by each instance (BFS). ~ = scaled estimate. Unbounded threshold: {unboundedThreshold:N0} entries per instance."
            : $"Own Size = direct object size only. Run 'load' to build BFS index for retained-size estimates. Unbounded threshold: {unboundedThreshold:N0} entries per instance.";

        sink.Table(
            ["Type", "Kind", "Instances", "Total Entries", "Avg Entries", "Max Entries", sizeHeader, "Status"],
            rows,
            sizeNote);

        // Donut: entries by collection kind
        var kindSegs = data.Entries
            .GroupBy(e => e.CollectionKind)
            .Select(g => (Label: g.Key, Value: (double)g.Sum(e => e.TotalEntryCount)))
            .OrderByDescending(s => s.Value)
            .ToList();
        if (kindSegs.Count > 1)
            sink.DonutChart(kindSegs, "Entries by collection kind",
                $"{data.TotalEntryCount:N0}\ntotal entries");
    }
}
