using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class HeapStatsReport
{
    public void Render(HeapStatsData data, IRenderSink sink,
        int top = 50, long minSize = 0, string sortBy = "size", string? genFilter = null)
    {
        var ordered = data.Types
            .Where(r => r.Size >= minSize)
            .OrderByDescending(r => sortBy == "count" ? r.Count : r.Size)
            .Take(top)
            .ToList();

        sink.Section("Heap Statistics");
        if (ordered.Count == 0) { sink.Text("No types match the specified filters."); return; }

        sink.KeyValues([
            ("Types shown",   ordered.Count.ToString("N0")),
            ("Types on heap", data.Types.Count.ToString("N0")),
            ("Total objects", data.TotalObjs.ToString("N0")),
            ("Total size",    DumpHelpers.FormatSize(data.TotalSize)),
        ]);

        var rows = ordered.Select(r => new[]
        {
            r.Name, r.Gen, r.Count.ToString("N0"),
            DumpHelpers.FormatSize(r.Size),
            data.TotalSize > 0 ? $"{r.Size * 100.0 / data.TotalSize:F1}%" : "?",
        }).ToList();

        sink.Table(["Type", "Gen", "Count", "Total Size", "% of Heap"], rows,
            $"Top {rows.Count} types by {sortBy}" + (genFilter is not null ? $" (gen={genFilter})" : ""));

        RenderGenericBloat(data, sink);
        RenderLoggingSection(data, sink);
    }

    private static void RenderGenericBloat(HeapStatsData data, IRenderSink sink)
    {
        var statsByName = data.Types.ToDictionary(r => r.Name, r => (r.Count, r.Size));
        var groups = data.Types
            .Where(r => r.Name.Contains('<'))
            .GroupBy(r => GetOpenGeneric(r.Name))
            .Where(g => g.Count() >= 5)
            .Select(g => new[]
            {
                g.Key, g.Count().ToString("N0"),
                g.Sum(r => r.Count).ToString("N0"),
                DumpHelpers.FormatSize(g.Sum(r => r.Size)),
            })
            .OrderByDescending(r => int.Parse(r[1].Replace(",", "")))
            .Take(20).ToList();

        if (groups.Count > 0)
        {
            sink.Section("Generic Type Specialization Bloat");
            sink.Alert(AlertLevel.Info,
                $"{groups.Count} generic type(s) with 5+ distinct closed-type specializations.",
                "Each distinct generic specialization (e.g., List<int>, List<string>) generates a separate JIT compilation, " +
                "contributing to code-size bloat and increased startup time.",
                "Prefer interfaces or base types for generic constraints where the concrete type doesn't affect performance.");
            sink.Table(["Open Generic", "Specializations", "Total Objects", "Total Size"], groups,
                "Generic types with ≥ 5 distinct closed specializations");
        }
    }

    private static void RenderLoggingSection(HeapStatsData data, IRenderSink sink)
    {
        var logPrefixes = new[] { "log4net.Core.LoggingEvent", "NLog.LogEventInfo", "Serilog.Events.LogEvent" };
        var logRows = data.Types
            .Where(r => logPrefixes.Any(p => r.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.Count)
            .Select(r => new[] { r.Name, r.Count.ToString("N0"), DumpHelpers.FormatSize(r.Size) })
            .ToList();

        if (logRows.Count > 0)
        {
            long logTotal = data.Types
                .Where(r => logPrefixes.Any(p => r.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                .Sum(r => r.Count);
            sink.Section("Logging Framework Accumulation");
            sink.Alert(
                logTotal > 10_000 ? AlertLevel.Critical : AlertLevel.Warning,
                $"{logTotal:N0} logging event object(s) from log4net/NLog/Serilog found on heap.",
                "Logging event objects accumulate when an appender is backing up or not flushing.",
                "Use a bounded async appender queue. Ensure Dispose/Flush is called on shutdown.");
            sink.Table(["Type", "Count", "Size"], logRows);
        }
    }

    private static string GetOpenGeneric(string t)
    {
        int bt = t.IndexOf('<');
        return bt >= 0 ? t[..bt] + "<>" : t;
    }
}
