using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class StaticRefsReport
{
    public void Render(StaticRefsData data, IRenderSink sink, bool showAddr = false)
    {
        sink.Section("Non-Null Static Reference Fields");
        if (data.Total == 0) { sink.Text("No non-null static reference fields found."); return; }

        sink.Explain(
            what: "Inventories all non-null static reference fields across all loaded types — these are permanent GC roots.",
            why: "Static fields are never collected unless explicitly nulled or the AppDomain unloads. Everything reachable from a static field lives forever.",
            impact: "A growing static collection or cache retains all added objects indefinitely, causing steady memory growth that survives GC.",
            bullets: ["'Collection fields' = static fields typed as List, Dictionary, ConcurrentDictionary, etc. — these grow unbounded", "'Retained size' is the estimated size of the entire object graph reachable from each field", "Largest declaring type often reveals the biggest problematic singleton"],
            action: "Replace static state with scoped DI registrations. Use WeakReference<T> or bounded caches (ConcurrentDictionary with TryAdd + eviction) for caches."
        );

        int  collections    = data.Fields.Count(f => f.IsCollection);
        var  sizeByDeclType = data.Fields.GroupBy(f => f.DeclType)
                                         .ToDictionary(g => g.Key, g => g.Sum(f => f.RetainedSize));
        int  declTypeCount  = sizeByDeclType.Count;
        var  largestDecl    = sizeByDeclType.Count > 0 ? sizeByDeclType.MaxBy(kv => kv.Value) : default;

        sink.KeyValues([
            ("Declaring types",        declTypeCount.ToString("N0")),
            ("Static fields",          data.Total.ToString("N0")),
            ("Total retained size",    DumpHelpers.FormatSize(data.TotalSize)
                                       + (data.IsEstimated ? "  ~" : "")),
            ("Largest declaring type", largestDecl.Key is null ? "—"
                : $"{largestDecl.Key.Split('.').Last()}  ({DumpHelpers.FormatSize(largestDecl.Value)})"
                  + (data.IsEstimated ? " ~" : "")),
            ("Collection fields",      collections.ToString("N0")),
            ("Size accuracy",          data.IsEstimated
                ? $"Estimated (sampling mode — run with --exact for precise values)"
                : "Exact (full BFS)"),
        ]);

        sink.Alert(AlertLevel.Info,
            "Static object references are permanent GC roots — they keep entire object graphs alive for the process lifetime.",
            "Prefer scoped DI registrations over static state. Use WeakReference<T> for caches.");

        RenderFieldAccordions(sink, data, showAddr);
    }

    private static void RenderFieldAccordions(IRenderSink sink, StaticRefsData data, bool showAddr)
    {
        // Group by declaring type
        var byDeclaringType = data.Fields
            .GroupBy(f => f.DeclType)
            .OrderByDescending(g => g.Sum(f => f.RetainedSize))
            .ToList();

        foreach (var group in byDeclaringType)
        {
            long groupSize     = group.Sum(f => f.RetainedSize);
            bool hasCollection = group.Any(f => f.IsCollection);
            sink.BeginDetails(
                $"{group.Key}  —  {group.Count()} field(s)  {DumpHelpers.FormatSize(groupSize)}"
                + (hasCollection ? "  ⚠ has collection" : ""),
                open: hasCollection || group.Count() > 5);

            var rows = group
                .OrderByDescending(f => f.RetainedSize)
                .Select(f =>
                {
                    var row = new List<string>
                    {
                        f.FieldName,
                        f.FieldType,
                        DumpHelpers.FormatSize(f.RetainedSize),
                        f.IsCollection ? "✓" : "—",
                    };
                    if (showAddr) row.Add($"0x{f.Addr:X16}");
                    return row.ToArray();
                }).ToList();

            var headers = showAddr
                ? new[] { "Field", "Value Type", "Size", "Collection?", "Address" }
                : new[] { "Field", "Value Type", "Size", "Collection?" };

            sink.Table(headers, rows);
            sink.EndDetails();
        }
    }
}
