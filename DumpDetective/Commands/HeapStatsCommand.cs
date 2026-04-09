using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;
using System.Diagnostics;

namespace DumpDetective.Commands;

internal static class HeapStatsCommand
{
    private const string Help = """
        Usage: DumpDetective heap-stats <dump-file> [options]

        Options:
          -n, --top <N>              Show top N types by size (default: 50)
          -s, --min-size <bytes>     Minimum total size to include
          -f, --filter <name>        Only show types whose name contains <name>
          --gen <0|1|2|loh|poh>      Filter to a specific generation
          -o, --output <file>        Write report to file (.md / .html / .txt)
          -h, --help                 Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        int top = 50; long minSize = 0; string? filter = null; string? genFilter = null;
        var (dumpPath, output) = CommandBase.ParseCommon(args);

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--top"      or "-n") && i + 1 < args.Length) int.TryParse(args[++i], out top);
            else if ((args[i] is "--min-size" or "-s") && i + 1 < args.Length) long.TryParse(args[++i], out minSize);
            else if ((args[i] is "--filter"   or "-f") && i + 1 < args.Length) filter = args[++i];
            else if (args[i] == "--gen" && i + 1 < args.Length) genFilter = args[++i].ToLowerInvariant();
        }

        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, top, minSize, filter, genFilter));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, int top = 50, long minSize = 0,
                                string? filter = null, string? genFilter = null)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — Heap Statistics",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap — dump may be incomplete."); return; }

        // mtToGen: MethodTable → segment kind label (first object seen in that segment)
        var mtToGen  = new Dictionary<ulong, string>();
        var stats    = new Dictionary<string, (long Count, long Size, string Gen)>(StringComparer.Ordinal);

        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Walking heap...", statusCtx =>
        {
            var watch  = Stopwatch.StartNew();
            long count = 0;

            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;

                // Generation label — cached per MethodTable
                string gen;
                if (!mtToGen.TryGetValue(obj.Type.MethodTable, out gen!))
                {
                    var seg = ctx.Heap.GetSegmentByAddress(obj.Address);
                    gen = seg?.Kind switch
                    {
                        GCSegmentKind.Generation0 => "Gen0",
                        GCSegmentKind.Generation1 => "Gen1",
                        GCSegmentKind.Generation2 => "Gen2",
                        GCSegmentKind.Large       => "LOH",
                        GCSegmentKind.Pinned      => "POH",
                        GCSegmentKind.Frozen      => "Frozen",
                        GCSegmentKind.Ephemeral   => EphemeralGen(seg!, obj.Address),
                        _                          => "Gen2",
                    };
                    mtToGen[obj.Type.MethodTable] = gen;
                }

                // Apply --gen filter
                if (genFilter is not null && !GenMatches(gen, genFilter)) continue;

                var name = obj.Type.Name ?? "<unknown>";
                if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

                long size = (long)obj.Size;
                if (stats.TryGetValue(name, out var e)) stats[name] = (e.Count + 1, e.Size + size, e.Gen);
                else                                    stats[name] = (1, size, gen);

                count++;
                if (watch.Elapsed.TotalSeconds >= 1)
                {
                    statusCtx.Status($"Walking heap — {count:N0} objects scanned, {stats.Count:N0} types...");
                    watch.Restart();
                }
            }
        });

        long totalSize  = stats.Values.Sum(v => v.Size);
        long totalObjs  = stats.Values.Sum(v => v.Count);

        var ordered = stats
            .Where(kv => kv.Value.Size >= minSize)
            .OrderByDescending(kv => kv.Value.Size)
            .Take(top)
            .ToList();

        var rows = ordered.Select(kv => new[]
        {
            kv.Key,
            kv.Value.Gen,
            kv.Value.Count.ToString("N0"),
            DumpHelpers.FormatSize(kv.Value.Size),
            totalSize > 0 ? $"{kv.Value.Size * 100.0 / totalSize:F1}%" : "?",
        }).ToList();

        sink.Section("Heap Statistics");
        if (rows.Count == 0) { sink.Text("No types match the specified filters."); return; }
        sink.Table(["Type", "Gen", "Count", "Total Size", "% of Heap"], rows,
            $"Top {rows.Count} types by size" + (genFilter is not null ? $" (gen={genFilter})" : ""));

        sink.KeyValues(
        [
            ("Types shown",   rows.Count.ToString("N0")),
            ("Types on heap", stats.Count.ToString("N0")),
            ("Total objects", totalObjs.ToString("N0")),
            ("Total size",    DumpHelpers.FormatSize(totalSize)),
        ]);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static string EphemeralGen(ClrSegment seg, ulong addr)
    {
        if (seg.Generation0.Contains(addr)) return "Gen0";
        if (seg.Generation1.Contains(addr)) return "Gen1";
        if (seg.Generation2.Contains(addr)) return "Gen2";
        return "Gen";
    }

    static bool GenMatches(string gen, string filter) => filter switch
    {
        "0"   => gen == "Gen0",
        "1"   => gen == "Gen1",
        "2"   => gen == "Gen2",
        "loh" => gen == "LOH",
        "poh" => gen == "POH",
        _     => true,
    };
}