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

        int collections = data.Fields.Count(f => f.IsCollection);
        var largest     = data.Fields.MaxBy(f => f.RetainedSize);

        sink.KeyValues([
            ("Static fields found",    data.Total.ToString("N0")),
            ("Total retained size",    DumpHelpers.FormatSize(data.TotalSize)),
            ("Collection fields",      collections.ToString("N0")),
            ("Largest field",          largest is null ? "—"
                : $"{largest.FieldName} on {largest.DeclType.Split('.').Last()}  ({DumpHelpers.FormatSize(largest.RetainedSize)})"),
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
            long groupSize = group.Sum(f => f.RetainedSize);
            sink.BeginDetails($"{group.Key}  ({group.Count()} fields  |  {DumpHelpers.FormatSize(groupSize)} retained)",
                open: groupSize > 1024 * 1024);

            var rows = group
                .OrderByDescending(f => f.RetainedSize)
                .Select(f =>
                {
                    var row = new List<string>
                    {
                        f.FieldName, f.FieldType.Split('<')[0].Split('.').Last(),
                        DumpHelpers.FormatSize(f.RetainedSize),
                        f.IsCollection ? "✓" : "",
                    };
                    if (showAddr) row.Add($"0x{f.Addr:X16}");
                    return row.ToArray();
                }).ToList();

            var headers = showAddr
                ? new[] { "Field", "Type", "Retained Size", "Collection?", "Address" }
                : new[] { "Field", "Type", "Retained Size", "Collection?" };

            sink.Table(headers, rows);
            sink.EndDetails();
        }
    }
}
