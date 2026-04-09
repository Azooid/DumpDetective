using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class FinalizerQueueCommand
{
    private const string Help = """
        Usage: DumpDetective finalizer-queue <dump-file> [options]

        Options:
          -n, --top <N>      Top N types (default: 30)
          -o, --output <f>   Write report to file
          -h, --help         Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;
        int top = 30;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        for (int i = 0; i < args.Length; i++)
            if ((args[i] is "--top" or "-n") && i + 1 < args.Length) int.TryParse(args[++i], out top);
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, top));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, int top = 30)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — Finalizer Queue",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        // (Count, TotalSize, hasDispose)
        var stats  = new Dictionary<string, (int Count, long Size, bool HasDispose)>(StringComparer.Ordinal);
        // Cache IDisposable check per MethodTable
        var disposeCache = new Dictionary<ulong, bool>();

        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Reading finalizer queue...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateFinalizableObjects())
            {
                if (!obj.IsValid) continue;
                var typeName = obj.Type?.Name ?? "<unknown>";
                long size    = (long)obj.Size;

                bool hasDispose = false;
                if (obj.Type is not null)
                {
                    if (!disposeCache.TryGetValue(obj.Type.MethodTable, out hasDispose))
                    {
                        hasDispose = obj.Type.Methods.Any(m => m.Name == "Dispose");
                        disposeCache[obj.Type.MethodTable] = hasDispose;
                    }
                }

                if (stats.TryGetValue(typeName, out var e))
                    stats[typeName] = (e.Count + 1, e.Size + size, e.HasDispose || hasDispose);
                else
                    stats[typeName] = (1, size, hasDispose);
            }
        });

        int  total     = stats.Values.Sum(v => v.Count);
        long totalSize = stats.Values.Sum(v => v.Size);

        sink.Section("Finalizer Queue Summary");
        if (total == 0) { sink.Text("Finalizer queue is empty — no finalizable objects found."); return; }

        sink.KeyValues([
            ("Total in queue",       total.ToString("N0")),
            ("Total size estimate",  DumpHelpers.FormatSize(totalSize)),
            ("Distinct types",       stats.Count.ToString("N0")),
            ("Types with Dispose()", stats.Count(kv => kv.Value.HasDispose).ToString("N0")),
        ]);

        // Advisory — always shown, regardless of count
        sink.Alert(AlertLevel.Info,
            "All objects in the finalizer queue delay GC collection of their entire retained object graph.",
            advice: "Call Dispose() / use 'using' statements to avoid finalizer pressure. Finalizers run on a single dedicated thread.");

        if (total >= 500)
            sink.Alert(AlertLevel.Critical, $"{total:N0} objects pending finalization.",
                advice: "A large finalizer queue indicates heavy GC pressure. Wrap IDisposable objects in 'using'.");
        else if (total >= 100)
            sink.Alert(AlertLevel.Warning, $"{total:N0} objects pending finalization.");

        var rows = stats
            .OrderByDescending(kv => kv.Value.Size)
            .Take(top)
            .Select(kv => new[]
            {
                kv.Key,
                kv.Value.Count.ToString("N0"),
                DumpHelpers.FormatSize(kv.Value.Size),
                kv.Value.HasDispose ? "✓" : "—",
            })
            .ToList();

        sink.Table(
            ["Type", "Count", "Total Size", "IDisposable"],
            rows,
            $"Top {rows.Count} types by size ({total:N0} total objects)");
    }
}
