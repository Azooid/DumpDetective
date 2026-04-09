using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class HeapStatsCommand
{
    private const string Help = """
        Usage: DumpDetective heap-stats <dump-file> [options]

        Options:
          -n, --top <N>          Show top N types by size (default: 50)
          -s, --min-size <bytes> Minimum total size to include
          -f, --filter <name>    Only show types whose name contains <name>
          -o, --output <file>    Write report to file (.md / .html / .txt)
          -h, --help             Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        int top = 50; long minSize = 0; string? filter = null;
        var (dumpPath, output) = CommandBase.ParseCommon(args);

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--top" or "-n") && i + 1 < args.Length)        int.TryParse(args[++i], out top);
            else if ((args[i] is "--min-size" or "-s") && i + 1 < args.Length) long.TryParse(args[++i], out minSize);
            else if ((args[i] is "--filter" or "-f") && i + 1 < args.Length)   filter = args[++i];
        }

        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, top, minSize, filter));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, int top = 50, long minSize = 0, string? filter = null)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);
        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap — dump may be incomplete."); return; }

        var stats = new Dictionary<string, (long Count, long Size)>(StringComparer.Ordinal);

        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Walking heap...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                var name = obj.Type.Name ?? "<unknown>";
                if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                long size = (long)obj.Size;
                if (stats.TryGetValue(name, out var e)) stats[name] = (e.Count + 1, e.Size + size);
                else stats[name] = (1, size);
            }
        });

        var rows = stats
            .Where(kv => kv.Value.Size >= minSize)
            .OrderByDescending(kv => kv.Value.Size)
            .Take(top)
            .Select(kv => new[] { kv.Key, kv.Value.Count.ToString("N0"), DumpHelpers.FormatSize(kv.Value.Size) })
            .ToList();

        sink.Section("Heap Statistics");
        sink.Table(["Type", "Count", "Total Size"], rows, $"Top {rows.Count} types by size");
        sink.KeyValues(
        [
            ("Types shown",    rows.Count.ToString("N0")),
            ("Types on heap",  stats.Count.ToString("N0")),
            ("Total objects",  stats.Values.Sum(v => v.Count).ToString("N0")),
            ("Total size",     DumpHelpers.FormatSize(stats.Values.Sum(v => v.Size))),
        ]);
    }
}