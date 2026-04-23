using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class TypeInstancesReport
{
    public void Render(TypeInstancesData data, IRenderSink sink, bool showAddr = false)
    {
        sink.Section($"Type Instances: {data.SearchTerm}");
        sink.Explain(
            what: $"Finds all instances of types matching '{data.SearchTerm}' on the managed heap and reports counts, sizes, and generation distribution.",
            why: "Useful for answering 'how many of this type exist?' — verifying singleton assumptions, investigating retention, or checking disposal.",
            impact: "Unexpected instance counts reveal incorrect lifetimes: transient services created as singletons, undisposed objects accumulating in Gen2/LOH.",
            bullets: ["Gen2 instances survived multiple GC cycles — they are candidates for retention/leak investigation", "LOH instances are large (\u226585 KB) and rarely collected — check if pooling is appropriate", "'Largest Instance' shows the biggest single allocation of that type"],
            action: "If count far exceeds expected, run gc-roots <dump> --type <TypeName> to trace what is keeping the instances alive."
        );

        if (data.ByType.Count == 0)
        {
            sink.Text($"No instances matching '{data.SearchTerm}' found.");
            return;
        }

        sink.KeyValues([
            ("Total instances",  data.TotalCount.ToString("N0")),
            ("Total size",       DumpHelpers.FormatSize(data.TotalSize)),
            ("Distinct types",   data.ByType.Count.ToString("N0")),
        ]);

        RenderTypeSummary(sink, data);
        RenderGenBreakdown(sink, data);
        RenderLargestInstances(sink, data, showAddr);
    }

    private static void RenderTypeSummary(IRenderSink sink, TypeInstancesData data)
    {
        var rows = data.ByType
            .OrderByDescending(kv => kv.Value.TotalSize)
            .Select(kv => new[]
            {
                kv.Key,
                kv.Value.Count.ToString("N0"),
                DumpHelpers.FormatSize(kv.Value.TotalSize),
                DumpHelpers.FormatSize(kv.Value.MaxSingle),
            }).ToList();
        sink.Table(["Exact Type", "Count", "Total Size", "Largest Instance"], rows);
    }

    private static void RenderGenBreakdown(IRenderSink sink, TypeInstancesData data)
    {
        sink.Section("Generation Distribution");
        long g0 = data.ByType.Values.Sum(v => v.Gen0);
        long g1 = data.ByType.Values.Sum(v => v.Gen1);
        long g2 = data.ByType.Values.Sum(v => v.Gen2);
        long loh = data.ByType.Values.Sum(v => v.Loh);
        sink.Table(["Gen", "Count", "% of Total"], [
            ["Gen0", g0.ToString("N0"), $"{g0 * 100.0 / Math.Max(1, data.TotalCount):F1}%"],
            ["Gen1", g1.ToString("N0"), $"{g1 * 100.0 / Math.Max(1, data.TotalCount):F1}%"],
            ["Gen2", g2.ToString("N0"), $"{g2 * 100.0 / Math.Max(1, data.TotalCount):F1}%"],
            ["LOH",  loh.ToString("N0"), $"{loh * 100.0 / Math.Max(1, data.TotalCount):F1}%"],
        ]);
    }

    private static void RenderLargestInstances(IRenderSink sink, TypeInstancesData data, bool showAddr)
    {
        sink.Section("Largest Instances");
        var allLargest = data.ByType.Values
            .SelectMany(v => v.LargestInstances)
            .OrderByDescending(e => e.Size)
            .Take(50)
            .ToList();

        var headers = showAddr
            ? new[] { "Size", "Gen", "Address" }
            : new[] { "Size", "Gen" };
        var rows = allLargest.Select(e =>
        {
            var row = new List<string> { DumpHelpers.FormatSize(e.Size), e.Gen };
            if (showAddr) row.Add($"0x{e.Addr:X16}");
            return row.ToArray();
        }).ToList();
        sink.Table(headers, rows);
    }
}
