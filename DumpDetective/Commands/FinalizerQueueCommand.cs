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
        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Reading finalizer queue...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateFinalizableObjects())
            {
                if (!obj.IsValid) continue;
                var name = obj.Type?.Name ?? "<unknown>";
                counts.TryGetValue(name, out int c); counts[name] = c + 1;
            }
        });

        int total = counts.Values.Sum();
        var rows = counts.OrderByDescending(kv => kv.Value).Take(top)
            .Select(kv => new[] { kv.Key, kv.Value.ToString("N0") }).ToList();

        sink.Section("Finalizer Queue");
        if (total == 0) { sink.Text("Finalizer queue is empty."); return; }
        sink.Table(["Type", "Count"], rows, $"Top {rows.Count} types ({total:N0} total)");

        if (total > 500) sink.Alert(AlertLevel.Warning, $"{total:N0} objects in finalizer queue.",
            advice: "Use Dispose() and \"using\" statements to avoid finalizer pressure.");
        sink.KeyValues([("Total in queue", total.ToString("N0")), ("Distinct types", counts.Count.ToString("N0"))]);
    }
}
