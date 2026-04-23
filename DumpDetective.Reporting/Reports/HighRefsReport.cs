using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class HighRefsReport
{
    public void Render(HighRefsData data, IRenderSink sink, int minRefs = 10, bool showAddr = false)
    {
        sink.Section("Summary");
        sink.Explain(
            what: "Identifies objects with the most inbound references — objects that many other objects point to simultaneously.",
            why: "Widely-shared objects act as implicit singletons. As long as one live root holds any of the referencing objects, the shared object (and its entire graph) cannot be collected.",
            impact: "One live root on a widely-shared object can retain hundreds or thousands of other objects, making it appear as if many unrelated objects are 'leaking'.",
            bullets: ["'Widely shared (≥10 types)' = different kinds of objects all reference this one — an ambient global", "'Cache-like' = Dictionary/List/ConcurrentDictionary types — verify they have eviction policies", "'Retained size' is the estimated graph kept alive through this object"],
            action: "Investigate whether widely-shared mutable objects should be immutable, split into smaller scopes, or replaced with explicit DI registrations with a scoped lifetime."
        );
        int maxRefs      = data.Candidates.Count > 0 ? data.Candidates[0].InboundRefs : 0;
        long totalHotSz  = data.Candidates.Sum(c => c.RetainedSize);
        int widelyShared = data.Candidates.Count(c => c.DistinctSourceTypes >= 10);
        int cacheLike    = data.Candidates.Count(c => Categorize(c.Type) is "Cache" or "Collection");

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
                $"A single object is referenced by {maxRefs:N0} other objects. One live root keeps ALL of them in memory.",
                "Review whether this object (or its owning container) should be scoped, pooled, or split.");
        else if (maxRefs >= 1_000)
            sink.Alert(AlertLevel.Warning, $"Peak inbound reference count is {maxRefs:N0}.",
                "Widely-shared objects extend the lifetime of every holder.",
                "Consider weak references or demand-loading for non-critical shared state.");

        if (widelyShared > 0)
            sink.Alert(AlertLevel.Warning,
                $"{widelyShared} object(s) are referenced from ≥ 10 distinct types — implicit global dependencies.",
                "Objects with many distinct referencing types are effectively ambient singletons. " +
                "They are difficult to mock, test, and scope independently.",
                "Prefer explicit dependency injection with scoped or transient lifetimes.");

        if (cacheLike > 0)
            sink.Alert(AlertLevel.Info,
                $"{cacheLike} hot object(s) appear to be caches or collections (Dictionary / List / ConcurrentDictionary).",
                "Shared mutable collections can grow without bound if no eviction policy is enforced.",
                "Verify that size limits, expiry policies, or bounded queues are in place.");

        RenderMainTable(data.Candidates, sink, showAddr, minRefs);
        RenderDetailAccordions(data.Candidates, sink);
        RenderHubDistribution(data.Candidates, sink);
        RenderGenDistribution(data.Candidates, sink);
        RenderRefHistogram(data.RefHistogram, sink);
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
        sink.Table(headers, tableRows, $"Sorted by inbound reference count  |  min-refs = {minRefs}  |  † Retained = own + direct children");
    }

    private static void RenderDetailAccordions(IReadOnlyList<HighRefEntry> candidates, IRenderSink sink)
    {
        sink.Section("Object Detail");
        foreach (var c in candidates)
        {
            bool open = c.InboundRefs >= 500 || c.DistinctSourceTypes >= 10;
            string category = Categorize(c.Type);
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
                ["Category",             category],
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

            if (category is "Cache" or "Collection")
                sink.Alert(AlertLevel.Info,
                    $"This {category.ToLower()} is referenced by {c.InboundRefs:N0} objects.",
                    "Shared mutable collections can grow without bound. Verify eviction and size limits.");
            else if (category == "String")
                sink.Alert(AlertLevel.Info,
                    $"String instance referenced {c.InboundRefs:N0} times.",
                    "Widely-shared strings are usually fine (flyweight). " +
                    "If this string is large, consider whether all holders need their own reference.");
            else if (c.DistinctSourceTypes >= 15)
                sink.Alert(AlertLevel.Warning,
                    $"Referenced from {c.DistinctSourceTypes} distinct types — implicit global dependency.",
                    "This object is acting as ambient shared state, coupling many unrelated types together.",
                    "Refactor to pass the dependency explicitly via constructor injection.");
            else if (category is "DI Container")
                sink.Alert(AlertLevel.Info,
                    $"DI/IoC container referenced from {c.DistinctSourceTypes} types.",
                    "Passing the container around as a service locator is an anti-pattern. " +
                    "Inject the specific service interfaces instead.");

            sink.EndDetails();
        }
    }

    private static string Categorize(string type)
    {
        if (type == "System.String" || type.StartsWith("System.String[", StringComparison.Ordinal))
            return "String";
        if (type.Contains("Dictionary",    StringComparison.OrdinalIgnoreCase) ||
            type.Contains("Cache",         StringComparison.OrdinalIgnoreCase) ||
            type.Contains("MemoryCache",   StringComparison.OrdinalIgnoreCase))
            return "Cache";
        if (type.Contains("List",          StringComparison.OrdinalIgnoreCase) ||
            type.Contains("[]")                                                  ||
            type.Contains("HashSet",       StringComparison.OrdinalIgnoreCase) ||
            type.Contains("Queue",         StringComparison.OrdinalIgnoreCase) ||
            type.Contains("Collection",    StringComparison.OrdinalIgnoreCase))
            return "Collection";
        if (type.Contains("Logger",        StringComparison.OrdinalIgnoreCase) ||
            (type.Contains("Log",          StringComparison.OrdinalIgnoreCase) &&
             !type.Contains("Dialog",      StringComparison.OrdinalIgnoreCase)))
            return "Logger";
        if (type.Contains("Configuration", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("Options",       StringComparison.OrdinalIgnoreCase) ||
            type.Contains("Settings",      StringComparison.OrdinalIgnoreCase))
            return "Config/Options";
        if (type.Contains("ServiceProvider", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("Container",     StringComparison.OrdinalIgnoreCase)   ||
            type.Contains("Factory",       StringComparison.OrdinalIgnoreCase))
            return "DI Container";
        if (type.Contains("HttpClient",    StringComparison.OrdinalIgnoreCase) ||
            type.Contains("DbContext",     StringComparison.OrdinalIgnoreCase) ||
            type.Contains("Connection",    StringComparison.OrdinalIgnoreCase))
            return "Resource Client";
        return "Object";
    }

    private static string ShortTypeName(string t)
    {
        int bt = t.IndexOf('`'), lt = t.IndexOf('<');
        int end = bt >= 0 && (lt < 0 || bt < lt) ? bt : lt >= 0 ? lt : t.Length;
        string trimmed = t[..end];
        int dot = trimmed.LastIndexOf('.');
        return dot >= 0 ? trimmed[(dot + 1)..] : trimmed;
    }

    // Like ShortTypeName but retains the full namespace prefix — hub-table grouping is per qualified base type.
    private static string SimplifiedTypeName(string t)
    {
        int bt = t.IndexOf('`'), lt = t.IndexOf('<');
        int end = bt >= 0 && (lt < 0 || bt < lt) ? bt : lt >= 0 ? lt : t.Length;
        return t[..end];
    }

    private static void RenderHubDistribution(IReadOnlyList<HighRefEntry> candidates, IRenderSink sink)
    {
        sink.Section("Hub Type Distribution");
        var hubTypes = candidates
            .GroupBy(c => SimplifiedTypeName(c.Type))
            .Select(g =>
            {
                int sumRef  = g.Sum(c => c.InboundRefs);
                int maxRef  = g.Max(c => c.InboundRefs);
                long totRet = g.Sum(c => c.RetainedSize);
                string cat  = Categorize(g.First().Type);
                return new[] { g.Key, cat, g.Count().ToString("N0"), sumRef.ToString("N0"),
                               maxRef.ToString("N0"), DumpHelpers.FormatSize(totRet) };
            })
            .OrderByDescending(r => int.Parse(r[3].Replace(",", "")))
            .ToList();
        if (hubTypes.Count > 0)
            sink.Table(
                ["Type Pattern", "Category", "Hot Instances", "Sum Inbound Refs", "Max Inbound Refs", "Retained Size (est.)"],
                hubTypes,
                "Hot types grouped by simplified name — reveals structural retention patterns");
    }

    private static void RenderGenDistribution(IReadOnlyList<HighRefEntry> candidates, IRenderSink sink)
    {
        sink.Section("Generation Distribution");
        var genDist = candidates
            .GroupBy(c => c.Gen)
            .OrderBy(g => g.Key switch { "Gen0" => 0, "Gen1" => 1, "Gen2" => 2, "LOH" => 3, "POH" => 4, _ => 9 })
            .Select(g => new[] {
                g.Key, g.Count().ToString("N0"),
                g.Sum(c => c.InboundRefs).ToString("N0"),
                DumpHelpers.FormatSize(g.Sum(c => c.RetainedSize)),
            }).ToList();
        if (genDist.Count > 0)
            sink.Table(["Generation", "Hot Objects", "Total Inbound Refs", "Retained Size (est.)"], genDist,
                "Gen2/LOH objects with many inbound refs from younger generations increase GC write-barrier cost");
        int gen2Count = candidates.Count(c => c.Gen is "Gen2" or "LOH");
        if (gen2Count > 0)
            sink.Alert(AlertLevel.Info,
                $"{gen2Count} of the hot objects reside in Gen2 or LOH.",
                "References from Gen0/Gen1 objects to these Gen2/LOH objects require GC write barriers and increase " +
                "card-table pressure. Every minor GC must scan these remembered-set entries.",
                "Minimise the number of short-lived objects that hold references to these long-lived hubs.");
    }

    private static void RenderRefHistogram(IReadOnlyList<(string Label, int Count)> histogram, IRenderSink sink)
    {
        if (histogram.Count == 0) return;
        sink.Section("Reference Count Distribution");
        var rows = histogram.Select(h => new[] { h.Label, h.Count.ToString("N0") }).ToList();
        sink.Table(["Inbound Ref Range", "Object Count"], rows,
            "Distribution of all objects by their inbound reference count (all objects ≥ 10)");
    }
}
