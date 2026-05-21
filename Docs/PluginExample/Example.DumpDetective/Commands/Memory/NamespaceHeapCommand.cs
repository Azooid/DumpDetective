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
public sealed class NamespaceHeapCommand : ICommand, ICommandHeapContributor
{
    private NamespaceHeapConsumer? _consumer;

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

    public IReadOnlyList<IHeapObjectConsumer> CreateHeapConsumers()
    {
        _consumer = new NamespaceHeapConsumer();
        return [_consumer];
    }

    public void PublishResults(DumpContext ctx)
    {
        if (_consumer is null) return;

        ctx.PreloadAnalysis(_consumer.ToCache());
        _consumer = null;
    }

    // ── analysis ─────────────────────────────────────────────────────────────

    private static void RenderWith(DumpContext ctx, IRenderSink sink, string? filter, int top)
    {
        CommandBase.RenderHeader("Heap by Namespace", ctx, sink);
        var cache = ctx.GetOrCreateAnalysis<NamespaceHeapCache>(() => NamespaceHeapCache.Build(ctx));

        var rows = cache.Totals
            .Where(kv => filter is null || kv.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Value.Size)
            .Take(top)
            .ToList();

        sink.KeyValues(
        [
            ("Total namespaces found", cache.Totals.Count.ToString("N0")),
            ("Total managed objects",  cache.TotalObjects.ToString("N0")),
            ("Total managed size",     DumpHelpers.FormatSize(cache.TotalSize)),
        ]);

        if (cache.Totals.Count == 0)
        {
            sink.Alert(AlertLevel.Info, "No objects found.",
                filter is not null ? $"No types match filter \"{filter}\"." : "The heap appears empty.");
            return;
        }

        // Donut chart — top 8 by size
        var chartSegments = rows
            .Take(8)
            .Select(kv => (kv.Key, (double)kv.Value.Size))
            .ToList();
        if (chartSegments.Count > 0)
        {
            sink.DonutChart(chartSegments,
                caption: "Top 8 namespaces by retained size",
                centerText: $"{DumpHelpers.FormatSize(cache.TotalSize)}\ntotal");
        }

        sink.Section("Top Namespaces by Size");
        sink.Table(
            ["Namespace", "Size", "% of Heap", "Objects"],
            rows.Select(kv => new[]
            {
                kv.Key,
                DumpHelpers.FormatSize(kv.Value.Size),
                cache.TotalSize > 0 ? $"{kv.Value.Size * 100.0 / cache.TotalSize:F1} %" : "—",
                kv.Value.Count.ToString("N0"),
            }).ToList(),
            caption: $"Top {rows.Count} of {cache.Totals.Count} namespace(s)" +
                     (filter is not null ? $" (filter: \"{filter}\")" : ""));
    }
}
