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
          --sort-by <size|count>     Sort order (default: size)
          -o, --output <file>        Write report to file (.md / .html / .txt)
          -h, --help                 Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        int top = 50; long minSize = 0; string? filter = null; string? genFilter = null; string sortBy = "size";
        var (dumpPath, output) = CommandBase.ParseCommon(args);

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--top"      or "-n") && i + 1 < args.Length) int.TryParse(args[++i], out top);
            else if ((args[i] is "--min-size" or "-s") && i + 1 < args.Length) long.TryParse(args[++i], out minSize);
            else if ((args[i] is "--filter"   or "-f") && i + 1 < args.Length) filter = args[++i];
            else if (args[i] == "--gen"      && i + 1 < args.Length) genFilter = args[++i].ToLowerInvariant();
            else if (args[i] == "--sort-by"  && i + 1 < args.Length) sortBy    = args[++i].ToLowerInvariant();
        }

        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, top, minSize, filter, genFilter, sortBy));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, int top = 50, long minSize = 0,
                                string? filter = null, string? genFilter = null, string sortBy = "size")
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
            .OrderByDescending(kv => sortBy == "count" ? kv.Value.Count : kv.Value.Size)
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
            $"Top {rows.Count} types by {sortBy}" + (genFilter is not null ? $" (gen={genFilter})" : ""));

        sink.KeyValues(
        [
            ("Types shown",   rows.Count.ToString("N0")),
            ("Types on heap", stats.Count.ToString("N0")),
            ("Total objects", totalObjs.ToString("N0")),
            ("Total size",    DumpHelpers.FormatSize(totalSize)),
        ]);

        // ── Generic type specialization bloat ─────────────────────────────────
        // Detect open-generic types with many distinct closed specializations
        var genericGroups = stats.Keys
            .Where(t => t.Contains('<'))
            .GroupBy(GetOpenGeneric)
            .Where(g => g.Count() >= 5)
            .Select(g =>
            {
                long gCount = g.Sum(t => stats[t].Count);
                long gSize  = g.Sum(t => stats[t].Size);
                return new[] { g.Key, g.Count().ToString("N0"), gCount.ToString("N0"), DumpHelpers.FormatSize(gSize) };
            })
            .OrderByDescending(r => int.Parse(r[1].Replace(",", "")))
            .Take(20)
            .ToList();
        if (genericGroups.Count > 0)
        {
            sink.Section("Generic Type Specialization Bloat");
            sink.Alert(AlertLevel.Info,
                $"{genericGroups.Count} generic type(s) with 5+ distinct closed-type specializations.",
                "Each distinct generic specialization (e.g., List<int>, List<string>) generates a separate JIT compilation, " +
                "contributing to code-size bloat and increased startup time.",
                "Prefer interfaces or base types for generic constraints where the concrete type doesn't affect performance.");
            sink.Table(["Open Generic", "Specializations", "Total Objects", "Total Size"], genericGroups,
                "Generic types with ≥ 5 distinct closed specializations");
        }

        // ── Logging framework event accumulation ──────────────────────────────
        var logPrefixes = new[]
        {
            "log4net.Core.LoggingEvent",
            "NLog.LogEventInfo",
            "Serilog.Events.LogEvent",
        };
        var logRows = stats
            .Where(kv => logPrefixes.Any(p => kv.Key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(kv => kv.Value.Count)
            .Select(kv => new[] { kv.Key, kv.Value.Count.ToString("N0"), DumpHelpers.FormatSize(kv.Value.Size) })
            .ToList();
        if (logRows.Count > 0)
        {
            long logTotal = stats
                .Where(kv => logPrefixes.Any(p => kv.Key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                .Sum(kv => kv.Value.Count);
            sink.Section("Logging Framework Accumulation");
            sink.Alert(
                logTotal > 10_000 ? AlertLevel.Critical : AlertLevel.Warning,
                $"{logTotal:N0} logging event object(s) from log4net/NLog/Serilog found on heap.",
                "Logging event objects on the heap indicate an appender is backing up or not flushing. " +
                "Possible causes: slow disk/network appender, async appender with unbounded queue, or shutdown before flush.",
                "Use a bounded async appender queue. Ensure Dispose/Flush is called on shutdown. Consider structured logging with lower retention.");
            sink.Table(["Type", "Count", "Size"], logRows);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static string GetOpenGeneric(string typeName)
    {
        int bt = typeName.IndexOf('<');
        return bt >= 0 ? typeName[..bt] + "<>" : typeName;
    }

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