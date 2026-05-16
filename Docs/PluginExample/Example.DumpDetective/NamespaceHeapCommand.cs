using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;

namespace Example.DumpDetective;

/// <summary>
/// Groups all live managed objects on the heap by the top-level namespace of their type
/// (e.g. "Newtonsoft", "System", "MyApp") and reports the total retained size and object
/// count per namespace.
///
/// Use this as a first pass when memory is higher than expected and you don't yet know
/// which library or subsystem to blame.  The donut chart of the top 8 namespaces gives an
/// instant visual breakdown of where managed memory is concentrated.
/// </summary>
public sealed class NamespaceHeapCommand : ICommand
{
    public string Name                 => "namespace-heap";
    public string Description          => "Break down heap memory usage by top-level namespace.";
    public bool   IncludeInFullAnalyze => true;
    public string Category             => "Example Plugin";
    public CommandKind Kind            => CommandKind.Memory;

    private const string Help = """
        Usage: DumpDetective namespace-heap <dump.dmp> [options]

        Walks the entire managed heap and groups objects by the top-level namespace of
        their type (e.g. "Newtonsoft", "System", "MyApp.Services").  Reports total size
        and object count per namespace, sorted by size descending.

        A donut chart of the top 8 namespaces gives a quick visual breakdown of where
        managed memory is concentrated.  Use this as a first pass when memory is high
        and you don't yet know which library or subsystem to blame.

        Options:
          -n, --top <N>    Show the top N namespaces (default: 20)
          -f, --filter <t> Only show namespaces that contain <t>
          -o, --output <f> Write report to file (.html / .md / .txt / .json)
          -h, --help       Show this help

        Examples:
          DumpDetective namespace-heap app.dmp
          DumpDetective namespace-heap app.dmp --top 30
          DumpDetective namespace-heap app.dmp --filter MyApp
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;
        var a   = CliArgs.Parse(args);
        int top = a.GetInt("top", 20);
        return CommandBase.Execute(a.DumpPath, a.EffectiveOutputPaths,
            (ctx, sink) => RenderWith(ctx, sink, a.Filter, top));
    }

    public void Render(DumpContext ctx, IRenderSink sink) => RenderWith(ctx, sink, null, 20);

    // ── analysis ─────────────────────────────────────────────────────────────

    private static void RenderWith(DumpContext ctx, IRenderSink sink, string? filter, int top)
    {
        CommandBase.RenderHeader("Heap by Namespace", ctx, sink);

        var heap = ctx.Runtime.Heap;

        // Accumulate size and count per top-level namespace.
        var totals = new Dictionary<string, (long Size, long Count)>(StringComparer.Ordinal);

        foreach (var obj in heap.EnumerateObjects())
        {
            if (!obj.IsValid) continue;
            var type = obj.Type;
            if (type is null) continue;

            string ns = TopLevelNamespace(type.Name ?? "");

            if (filter is not null &&
                !ns.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            ref var entry = ref System.Runtime.InteropServices.CollectionsMarshal
                .GetValueRefOrAddDefault(totals, ns, out _);
            entry.Size  += (long)obj.Size;
            entry.Count += 1;
        }

        long totalSize    = totals.Values.Sum(v => v.Size);
        long totalObjects = totals.Values.Sum(v => v.Count);

        sink.KeyValues(
        [
            ("Total namespaces found", totals.Count.ToString("N0")),
            ("Total managed objects",  totalObjects.ToString("N0")),
            ("Total managed size",     DumpHelpers.FormatSize(totalSize)),
        ]);

        if (totals.Count == 0)
        {
            sink.Alert(AlertLevel.Info, "No objects found.",
                filter is not null ? $"No types match filter \"{filter}\"." : "The heap appears empty.");
            return;
        }

        var rows = totals
            .OrderByDescending(kv => kv.Value.Size)
            .Take(top)
            .ToList();

        // Donut chart — top 8 by size
        var chartSegments = rows
            .Take(8)
            .Select(kv => (kv.Key, (double)kv.Value.Size))
            .ToList();
        if (chartSegments.Count > 0)
        {
            sink.DonutChart(chartSegments,
                caption: "Top 8 namespaces by retained size",
                centerText: $"{DumpHelpers.FormatSize(totalSize)}\ntotal");
        }

        sink.Section("Top Namespaces by Size");
        sink.Table(
            ["Namespace", "Size", "% of Heap", "Objects"],
            rows.Select(kv => new[]
            {
                kv.Key,
                DumpHelpers.FormatSize(kv.Value.Size),
                totalSize > 0 ? $"{kv.Value.Size * 100.0 / totalSize:F1} %" : "—",
                kv.Value.Count.ToString("N0"),
            }).ToList(),
            caption: $"Top {rows.Count} of {totals.Count} namespace(s)" +
                     (filter is not null ? $" (filter: \"{filter}\")" : ""));
    }

    /// <summary>
    /// Extracts the first component of a dotted namespace name.
    /// "Newtonsoft.Json.Linq.JObject" → "Newtonsoft"
    /// "System.Collections.Generic.List`1" → "System"
    /// "&lt;anonymous&gt;" → "&lt;anonymous&gt;"
    /// </summary>
    private static string TopLevelNamespace(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return "<unknown>";

        int dot = typeName.IndexOf('.');
        if (dot <= 0) return typeName;          // no dot — treat whole name as the "namespace"

        string top = typeName[..dot];

        // Compiler-generated types begin with '<' — group them together
        if (top.Length > 0 && top[0] == '<') return "<compiler-generated>";

        return top;
    }
}
