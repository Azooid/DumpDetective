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

        // Top declaring types by retained size — donut
        if (sizeByDeclType.Count > 1)
        {
            var typeSegs = sizeByDeclType
                .OrderByDescending(kv => kv.Value)
                .Take(8)
                .Select(kv => {
                    string lbl = kv.Key.Contains('.') ? kv.Key[(kv.Key.LastIndexOf('.')+1)..] : kv.Key;
                    if (lbl.Length > 28) lbl = lbl[..28] + "\u2026";
                    return (Label: lbl, Value: (double)kv.Value);
                })
                .ToList();
            sink.DonutChart(typeSegs, "Top 8 declaring types by retained static size",
                DumpHelpers.FormatSize(data.TotalSize) + "\ntotal");
        }
        sink.Alert(AlertLevel.Info,
            "Static object references are permanent GC roots — they keep entire object graphs alive for the process lifetime.",
            "Prefer scoped DI registrations over static state. Use WeakReference<T> for caches.");

        if (data.SkippedModuleCount > 0)
            sink.Alert(AlertLevel.Warning,
                $"{data.SkippedModuleCount} module(s) skipped due to corrupt or inconsistent PE metadata.",
                "These modules could not be enumerated for static fields. Results may be incomplete. " +
                "This typically affects dynamic modules, mixed-mode assemblies, or partially-loaded modules in the dump.");

        sink.BeginDetails(
            $"Object Reference Fields \u2014 {data.Fields.Count:N0} field(s) across {declTypeCount} type(s)" +
            $"  \u00b7  {DumpHelpers.FormatSize(data.TotalSize)} retained" +
            (data.IsEstimated ? "  (estimated)" : ""),
            open: true);
        RenderFieldAccordions(sink, data, showAddr);
        sink.EndDetails();

        if (data.NonRefFields is { Count: > 0 } nonRef)
            RenderNonRefAccordion(sink, nonRef);
    }

    private static void RenderNonRefAccordion(IRenderSink sink, IReadOnlyList<NonRefStaticFieldEntry> fields)
    {
        var byType = fields
            .GroupBy(f => f.DeclType)
            .OrderByDescending(g => g.Count())
            .ToList();

        int enumCount      = fields.Count(f => f.ElementKind == "Enum");
        int primitiveCount = fields.Count(f => f.ElementKind == "Primitive");
        int structCount    = fields.Count(f => f.ElementKind == "Struct");
        int pointerCount   = fields.Count(f => f.ElementKind == "Pointer");

        var parts = new List<string>();
        if (primitiveCount > 0) parts.Add($"{primitiveCount:N0} primitive(s)");
        if (enumCount      > 0) parts.Add($"{enumCount:N0} enum(s)");
        if (structCount    > 0) parts.Add($"{structCount:N0} struct(s)");
        if (pointerCount   > 0) parts.Add($"{pointerCount:N0} pointer(s)");

        sink.BeginDetails(
            $"Value Type Fields \u2014 {fields.Count:N0} field(s) across {byType.Count} type(s)" +
            (parts.Count > 0 ? $"  [{string.Join(" \u00b7 ", parts)}]" : ""),
            open: false);

        foreach (var group in byType)
        {
            int eCount = group.Count(f => f.ElementKind == "Enum");
            int pCount = group.Count(f => f.ElementKind == "Primitive");
            int sCount = group.Count(f => f.ElementKind == "Struct");

            var subParts = new List<string>();
            if (pCount > 0) subParts.Add($"{pCount} primitive(s)");
            if (eCount > 0) subParts.Add($"{eCount} enum(s)");
            if (sCount > 0) subParts.Add($"{sCount} struct(s)");

            sink.BeginDetails(
                $"{group.Key}  \u2014  {group.Count()} field(s)" +
                (subParts.Count > 0 ? $"  [{string.Join(" \u00b7 ", subParts)}]" : ""),
                open: false);

            bool hasValues = group.Any(f => f.Value is not null);
            var headers = hasValues
                ? new[] { "Field", "Type", "Kind", "Value" }
                : new[] { "Field", "Type", "Kind" };

            var rows = group
                .OrderBy(f => f.ElementKind)
                .ThenBy(f => f.FieldName)
                .Select(f => hasValues
                    ? new[] { f.FieldName, f.FieldType, f.ElementKind, f.Value ?? "\u2014" }
                    : new[] { f.FieldName, f.FieldType, f.ElementKind })
                .ToList();

            sink.Table(headers, rows);
            sink.EndDetails();
        }

        sink.EndDetails();
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
