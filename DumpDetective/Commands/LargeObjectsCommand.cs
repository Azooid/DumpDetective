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
          --type-breakdown       Show aggregate size by type instead of individual objects
          -o, --output <file>    Write report to file (.md / .html / .txt)
          -h, --help             Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        int top = 50; long minSize = 85_000; string? filter = null; bool showAddr = false; bool typeBreakdown = false;
        var (dumpPath, output) = CommandBase.ParseCommon(args);

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--top"      or "-n") && i + 1 < args.Length) int.TryParse(args[++i], out top);
            else if ((args[i] is "--min-size" or "-s") && i + 1 < args.Length) long.TryParse(args[++i], out minSize);
            else if ((args[i] is "--filter"   or "-f") && i + 1 < args.Length) filter = args[++i];
            else if (args[i] is "--addresses" or "-a")   showAddr      = true;
            else if (args[i] == "--type-breakdown")      typeBreakdown = true;
        }

        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, top, minSize, filter, showAddr, typeBreakdown));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink,
        int top = 50, long minSize = 85_000, string? filter = null,
        bool showAddr = false, bool typeBreakdown = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — Large Objects",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap — dump may be incomplete."); return; }

        var tuples = new List<(string Type, string ElemType, long Size, string Seg, string Addr)>();

        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start($"Finding objects ≥ {DumpHelpers.FormatSize(minSize)}...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                long size = (long)obj.Size;
                if (size < minSize) continue;
                var name = obj.Type.Name ?? "<unknown>";
                if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                // Element type for arrays
                string elemType = obj.Type.ComponentType?.Name ?? "";
                tuples.Add((name, elemType, size, DumpHelpers.SegmentKindLabel(ctx.Heap, obj.Address), $"0x{obj.Address:X16}"));
            }
        });

        if (tuples.Count == 0) { sink.Text($"No objects ≥ {DumpHelpers.FormatSize(minSize)} found."); return; }

        sink.KeyValues([
            ("Objects found",   tuples.Count.ToString("N0")),
            ("Total size",      DumpHelpers.FormatSize(tuples.Sum(r => r.Size))),
            ("Largest object",  DumpHelpers.FormatSize(tuples.Max(r => r.Size))),
        ]);

        // ── Type aggregate breakdown ─────────────────────────────────────────
        var typeAgg = tuples
            .GroupBy(r => r.Type)
            .Select(g => (
                Type:     g.Key,
                ElemType: g.First().ElemType,
                Count:    g.Count(),
                Size:     g.Sum(r => r.Size)))
            .OrderByDescending(t => t.Size)
            .ToList();

        if (typeBreakdown || true) // always show type aggregate — it's the most useful view
        {
            var aggRows = typeAgg.Take(top).Select(t => new[]
            {
                t.Type,
                t.ElemType.Length > 0 ? t.ElemType : "—",
                t.Count.ToString("N0"),
                DumpHelpers.FormatSize(t.Size),
                tuples.Sum(r => r.Size) > 0 ? $"{t.Size * 100.0 / tuples.Sum(r => r.Size):F1}%" : "?",
            }).ToList();
            sink.Section("Type Aggregate");
            sink.Table(["Type", "Element Type", "Count", "Total Size", "% of LOH+"], aggRows,
                $"Top {aggRows.Count} types ≥ {DumpHelpers.FormatSize(minSize)}");
        }

        // ── Individual largest objects table ─────────────────────────────────
        if (!typeBreakdown)
        {
            var sorted = tuples.OrderByDescending(r => r.Size).Take(top).ToList();
            string[] headers = showAddr
                ? ["Type", "Element Type", "Size", "Segment", "Address"]
                : ["Type", "Element Type", "Size", "Segment"];
            var rows = sorted.Select(r => showAddr
                ? new[] { r.Type, r.ElemType.Length > 0 ? r.ElemType : "—", DumpHelpers.FormatSize(r.Size), r.Seg, r.Addr }
                : new[] { r.Type, r.ElemType.Length > 0 ? r.ElemType : "—", DumpHelpers.FormatSize(r.Size), r.Seg }
            ).ToList();

            sink.Section($"Top {sorted.Count} Largest Individual Objects");
            sink.Table(headers, rows, $"Top {sorted.Count} of {tuples.Count:N0} objects ≥ {DumpHelpers.FormatSize(minSize)}");
        }

        // ── Segment breakdown ─────────────────────────────────────────────────
        var bySegment = tuples.GroupBy(r => r.Seg)
            .Select(g => new[] { g.Key, g.Count().ToString("N0"), DumpHelpers.FormatSize(g.Sum(r => r.Size)) })
            .OrderByDescending(r => r[2])
            .ToList();
        sink.Table(["Segment", "Objects", "Total Size"], bySegment, "By segment");
    }
}
