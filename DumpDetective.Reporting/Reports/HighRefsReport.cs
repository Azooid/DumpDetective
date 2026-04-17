using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class HighRefsReport
{
    public void Render(HighRefsData data, IRenderSink sink, int minRefs = 10, bool showAddr = false)
    {
        sink.Section("Summary");
        int maxRefs      = data.Candidates.Count > 0 ? data.Candidates[0].InboundRefs : 0;
        long totalHotSz  = data.Candidates.Sum(c => c.RetainedSize);
        int widelyShared = data.Candidates.Count(c => c.DistinctSourceTypes >= 10);
        int cacheLike    = data.Candidates.Count(c => IsCacheLike(c.Type));

        sink.KeyValues([
            ("Objects scanned",             data.TotalObjs.ToString("N0")),
            ("Total object references",     data.TotalRefs.ToString("N0")),
            ("Unique referenced addresses", data.UniqueReferencedAddrs.ToString("N0")),
            ("Hot objects (≥ min-refs)",    $"{data.Candidates.Count:N0}  (threshold: {minRefs:N0})"),
            ("Peak inbound ref count",      maxRefs.ToString("N0")),
            ("Widely shared (≥ 10 types)",  widelyShared.ToString("N0")),
            ("Cache-like hot objects",      cacheLike.ToString("N0")),
            ("Total retained size (est.)", DumpHelpers.FormatSize(totalHotSz)),
        ]);

        if (data.Candidates.Count == 0)
        {
            sink.Alert(AlertLevel.Info, $"No objects with ≥ {minRefs} inbound references found.");
            return;
        }

        if (maxRefs >= 10_000)
            sink.Alert(AlertLevel.Critical, $"Peak inbound reference count is {maxRefs:N0} — extreme shared-state detected.",
                "A single object is referenced by many others — one live root keeps ALL of them in memory.",
                "Review whether this object should be scoped, pooled, or split.");
        else if (maxRefs >= 1_000)
            sink.Alert(AlertLevel.Warning, $"Peak inbound reference count is {maxRefs:N0}.",
                "Widely-shared objects extend the lifetime of every holder.",
                "Consider weak references or demand-loading for non-critical shared state.");

        if (widelyShared > 0)
            sink.Alert(AlertLevel.Warning,
                $"{widelyShared} object(s) referenced from ≥ 10 distinct types — implicit global dependencies.",
                "Objects with many distinct referencing types are effectively ambient singletons.",
                "Prefer explicit dependency injection with scoped or transient lifetimes.");

        RenderMainTable(data.Candidates, sink, showAddr, minRefs);
        RenderDetailAccordions(data.Candidates, sink);
    }

    private static void RenderMainTable(IReadOnlyList<HighRefEntry> candidates, IRenderSink sink,
        bool showAddr, int minRefs)
    {
        sink.Section($"Top {candidates.Count} Highly-Referenced Objects");
        var tableRows = candidates.Select(c =>
        {
            string topSrc = c.TopSources.Count > 0 ? ShortTypeName(c.TopSources[0].Type) : "—";
            var row = new List<string>
            {
                c.Type, c.InboundRefs.ToString("N0"), c.DistinctSourceTypes.ToString("N0"),
                topSrc, c.Gen, DumpHelpers.FormatSize(c.OwnSize), DumpHelpers.FormatSize(c.RetainedSize),
            };
            if (showAddr) row.Insert(0, $"0x{c.Addr:X16}");
            return row.ToArray();
        }).ToList();

        var headers = showAddr
            ? new[] { "Address", "Type", "Inbound Refs", "Distinct Ref Types", "Top Source", "Gen", "Own Size", "Retained† Size" }
            : new[] { "Type", "Inbound Refs", "Distinct Ref Types", "Top Source", "Gen", "Own Size", "Retained† Size" };
        sink.Table(headers, tableRows, $"Sorted by inbound ref count  |  min-refs = {minRefs}  |  † Retained = own + direct children");
    }

    private static void RenderDetailAccordions(IReadOnlyList<HighRefEntry> candidates, IRenderSink sink)
    {
        sink.Section("Object Detail");
        foreach (var c in candidates)
        {
            bool open = c.InboundRefs >= 500 || c.DistinctSourceTypes >= 10;
            sink.BeginDetails(
                $"{c.Type}  |  {c.InboundRefs:N0} inbound refs  |  {c.Gen}  |  {DumpHelpers.FormatSize(c.RetainedSize)} retained",
                open: open);

            sink.Table(["Property", "Value"],
            [
                ["Full Type",            c.Type],
                ["Address",              $"0x{c.Addr:X16}"],
                ["Own Size",             DumpHelpers.FormatSize(c.OwnSize)],
                ["Retained Size (est.)", DumpHelpers.FormatSize(c.RetainedSize)],
                ["Generation",           c.Gen],
                ["Inbound Refs",         c.InboundRefs.ToString("N0")],
                ["Distinct Ref Types",   c.DistinctSourceTypes.ToString("N0")],
            ]);

            if (c.TopSources.Count > 0)
            {
                var srcRows = c.TopSources.Select(s => new[]
                {
                    s.Type, s.Count.ToString("N0"),
                    $"{s.Count * 100.0 / Math.Max(1, c.InboundRefs):F1}%",
                }).ToList();
                sink.Table(["Referencing Type", "Count", "% of Total"], srcRows,
                    $"Top {srcRows.Count} of {c.DistinctSourceTypes} distinct referencing types");
            }
            sink.EndDetails();
        }
    }

    private static bool IsCacheLike(string t) =>
        t.Contains("Dictionary", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("Cache",      StringComparison.OrdinalIgnoreCase) ||
        t.Contains("List",       StringComparison.OrdinalIgnoreCase) ||
        t.Contains("HashSet",    StringComparison.OrdinalIgnoreCase) ||
        t.Contains("Queue",      StringComparison.OrdinalIgnoreCase);

    private static string ShortTypeName(string t)
    {
        int bt = t.IndexOf('`'), lt = t.IndexOf('<');
        int end = bt >= 0 && (lt < 0 || bt < lt) ? bt : lt >= 0 ? lt : t.Length;
        string trimmed = t[..end];
        int dot = trimmed.LastIndexOf('.');
        return dot >= 0 ? trimmed[(dot + 1)..] : trimmed;
    }
}
