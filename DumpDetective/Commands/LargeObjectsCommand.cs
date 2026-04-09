using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class LargeObjectsCommand
{
    private const string Help = """
        Usage: DumpDetective large-objects <dump-file> [options]

        Options:
          -n, --top <N>          Show top N objects by size (default: 50)
          -s, --min-size <bytes> Minimum object size (default: 85000)
          -f, --filter <name>    Only show types whose name contains <name>
          -a, --addresses        Show object addresses
          -o, --output <file>    Write report to file (.md / .html / .txt)
          -h, --help             Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        int top = 50; long minSize = 85_000; string? filter = null; bool showAddr = false;
        var (dumpPath, output) = CommandBase.ParseCommon(args);

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--top" or "-n") && i + 1 < args.Length)        int.TryParse(args[++i], out top);
            else if ((args[i] is "--min-size" or "-s") && i + 1 < args.Length) long.TryParse(args[++i], out minSize);
            else if ((args[i] is "--filter" or "-f") && i + 1 < args.Length)   filter = args[++i];
            else if (args[i] is "--addresses" or "-a") showAddr = true;
        }

        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, top, minSize, filter, showAddr));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, int top = 50, long minSize = 85_000, string? filter = null, bool showAddr = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);
        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap — dump may be incomplete."); return; }

        var tuples = new List<(string Type, long Size, string Seg, string Addr)>();

        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start($"Finding objects ≥ {DumpHelpers.FormatSize(minSize)}...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                long size = (long)obj.Size;
                if (size < minSize) continue;
                var name = obj.Type.Name ?? "<unknown>";
                if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                tuples.Add((name, size, DumpHelpers.SegmentKindLabel(ctx.Heap, obj.Address), $"0x{obj.Address:X16}"));
            }
        });

        var sorted = tuples.OrderByDescending(r => r.Size).Take(top).ToList();
        string[] headers = showAddr ? ["Type", "Size", "Segment", "Address"] : ["Type", "Size", "Segment"];
        var rows = sorted.Select(r => showAddr
            ? new[] { r.Type, DumpHelpers.FormatSize(r.Size), r.Seg, r.Addr }
            : new[] { r.Type, DumpHelpers.FormatSize(r.Size), r.Seg }).ToList();

        sink.Section("Large Objects");
        sink.Table(headers, rows, $"Top {sorted.Count} objects ≥ {DumpHelpers.FormatSize(minSize)}");

        var bySegment = tuples.GroupBy(r => r.Seg)
            .Select(g => new[] { g.Key, g.Count().ToString("N0"), DumpHelpers.FormatSize(g.Sum(r => r.Size)) })
            .OrderByDescending(r => r[2]).ToList();
        sink.Table(["Segment", "Objects", "Total Size"], bySegment, "By segment");

        sink.KeyValues(
        [
            ("Objects shown",  $"{sorted.Count:N0} of {tuples.Count:N0}"),
            ("Size shown",     DumpHelpers.FormatSize(sorted.Sum(r => r.Size))),
            ("Total LOH+",     DumpHelpers.FormatSize(tuples.Sum(r => r.Size))),
        ]);
    }
}
