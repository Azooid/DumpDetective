using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;
using System.Diagnostics;

namespace DumpDetective.Commands;

internal static class TypeInstancesCommand
{
    private const string Help = """
        Usage: DumpDetective type-instances <dump-file> --type <name> [options]

        Options:
          -t, --type <name>    Type name to search (case-insensitive substring)  [required]
          -n, --top <N>        Max instances to show in detail (default: 50)
          -a, --addresses      Show individual object addresses
          --min-size <bytes>   Only include instances larger than N bytes
          --gen <0|1|2|loh>    Only include instances in the specified generation
          -o, --output <f>     Write report to file
          -h, --help           Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        string? typeName = null; int top = 50; bool showAddr = false;
        long minSize = 0; string? genFilter = null;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--type" or "-t") && i + 1 < args.Length)      typeName  = args[++i];
            else if ((args[i] is "--top" or "-n") && i + 1 < args.Length)  int.TryParse(args[++i], out top);
            else if (args[i] is "--addresses" or "-a")                      showAddr  = true;
            else if (args[i] == "--min-size"  && i + 1 < args.Length)      long.TryParse(args[++i], out minSize);
            else if (args[i] == "--gen"       && i + 1 < args.Length)      genFilter = args[++i].ToLowerInvariant();
        }

        if (typeName is null) { AnsiConsole.MarkupLine("[bold red]✗[/] --type is required."); return 1; }
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, typeName, top, showAddr, minSize, genFilter));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink,
                                string typeName, int top = 50, bool showAddr = false,
                                long minSize = 0, string? genFilter = null)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);
        sink.Header(
            "Dump Detective — Type Instances",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        // Type → (Count, TotalSize, Gen0c, Gen1c, Gen2c, LOHc, MaxSingle, list of top-N largest)
        var typeStats = new Dictionary<string, TypeData>(StringComparer.Ordinal);
        long totalScanned = 0;

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("blue"))
            .Start($"Finding instances of '{typeName}'...", status =>
            {
                var watch = Stopwatch.StartNew();
                foreach (var obj in ctx.Heap.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                    if (obj.Type.Name?.Contains(typeName, StringComparison.OrdinalIgnoreCase) != true) continue;

                    string gen = GetGenLabel(ctx, obj.Address);
                    long size  = (long)obj.Size;

                    if (minSize > 0 && size < minSize) { totalScanned++; continue; }
                    if (genFilter is not null && !GenMatches(gen, genFilter)) { totalScanned++; continue; }

                    string key = obj.Type.Name!;
                    if (!typeStats.TryGetValue(key, out var td))
                    {
                        td = new TypeData(key, HasIDisposable(obj.Type));
                        typeStats[key] = td;
                    }
                    td.Add(size, gen, obj.Address);
                    totalScanned++;

                    if (watch.Elapsed.TotalSeconds >= 1)
                    {
                        status.Status($"Scanning — {totalScanned:N0} objects, {typeStats.Values.Sum(t => t.Count):N0} matches...");
                        watch.Restart();
                    }
                }
            });

        sink.Section($"Type Instances: {typeName}");
        if (typeStats.Count == 0)
        {
            sink.Text($"No instances matching '{typeName}' found" +
                      (genFilter is not null ? $" in generation '{genFilter}'" : "") +
                      (minSize > 0 ? $" with size ≥ {DumpHelpers.FormatSize(minSize)}" : "") + ".");
            return;
        }

        long totalCount = typeStats.Values.Sum(t => (long)t.Count);
        long totalSize  = typeStats.Values.Sum(t => t.TotalSize);

        // ── Type summary by size ───────────────────────────────────────────────
        var summaryRows = typeStats.Values
            .OrderByDescending(t => t.TotalSize)
            .Select(t => new[]
            {
                t.TypeName,
                t.Count.ToString("N0"),
                DumpHelpers.FormatSize(t.TotalSize),
                DumpHelpers.FormatSize(t.MaxSize),
                t.HasDispose ? "✓" : "—",
            })
            .ToList();
        sink.Table(
            ["Type", "Count", "Total Size", "Largest", "IDisposable"],
            summaryRows,
            $"{typeStats.Count} distinct type(s) — {totalCount:N0} instances — {DumpHelpers.FormatSize(totalSize)} total");

        // ── Generation breakdown ───────────────────────────────────────────────
        var genTotal = new Dictionary<string, (long Count, long Size)>(StringComparer.Ordinal);
        foreach (var td in typeStats.Values)
        {
            foreach (var kv in td.GenCounts)
            {
                if (!genTotal.TryGetValue(kv.Key, out var gs)) genTotal[kv.Key] = (0, 0);
                var curr = genTotal[kv.Key];
                long genSize = td.GenSizes.GetValueOrDefault(kv.Key, 0);
                genTotal[kv.Key] = (curr.Count + kv.Value, curr.Size + genSize);
            }
        }
        if (genTotal.Count > 0)
        {
            var genRows = genTotal
                .OrderBy(kv => GenSortKey(kv.Key))
                .Select(kv => new[] { kv.Key, kv.Value.Count.ToString("N0"), DumpHelpers.FormatSize(kv.Value.Size) })
                .ToList();
            sink.Table(["Generation", "Count", "Total Size"], genRows, "Instance distribution by generation");
        }

        // ── Largest instances ──────────────────────────────────────────────────
        var largestInstances = typeStats.Values
            .SelectMany(t => t.LargestAddrs)
            .OrderByDescending(x => x.Size)
            .Take(10)
            .ToList();
        if (largestInstances.Count > 0)
        {
            var largeRows = largestInstances
                .Select(x => new[] { x.TypeName, $"0x{x.Addr:X16}", DumpHelpers.FormatSize(x.Size) })
                .ToList();
            sink.Table(["Type", "Address", "Size"], largeRows, "Top 10 largest instances");
        }

        // ── Per-address table ──────────────────────────────────────────────────
        if (showAddr)
        {
            foreach (var td in typeStats.Values.OrderByDescending(t => t.TotalSize))
            {
                var addrRows = td.LargestAddrs
                    .Take(top)
                    .Select(x => new[] { $"0x{x.Addr:X16}", DumpHelpers.FormatSize(x.Size) })
                    .ToList();
                if (addrRows.Count == 0) continue;
                sink.BeginDetails($"{td.TypeName} — {td.Count:N0} instance(s)", open: false);
                sink.Table(["Address", "Size"], addrRows);
                sink.EndDetails();
            }
        }

        sink.KeyValues([
            ("Total instances", totalCount.ToString("N0")),
            ("Total size",      DumpHelpers.FormatSize(totalSize)),
            ("Distinct types",  typeStats.Count.ToString("N0")),
        ]);
    }

    // Per-type tracking data
    private sealed class TypeData(string typeName, bool hasDispose)
    {
        public string TypeName  { get; } = typeName;
        public bool   HasDispose { get; } = hasDispose;
        public long   Count     { get; private set; }
        public long   TotalSize { get; private set; }
        public long   MaxSize   { get; private set; }

        public Dictionary<string, long> GenCounts = new(StringComparer.Ordinal);
        public Dictionary<string, long> GenSizes  = new(StringComparer.Ordinal);
        // Keep up to 50 largest for address/detail display
        public List<(ulong Addr, long Size, string TypeName)> LargestAddrs { get; } = new(capacity: 50);

        public void Add(long size, string gen, ulong addr)
        {
            Count++;
            TotalSize += size;
            if (size > MaxSize) MaxSize = size;

            if (!GenCounts.ContainsKey(gen)) { GenCounts[gen] = 0; GenSizes[gen] = 0; }
            GenCounts[gen]++;
            GenSizes[gen] += size;

            if (LargestAddrs.Count < 50)
                LargestAddrs.Add((addr, size, TypeName));
            else if (size > LargestAddrs.Min(x => x.Size))
            {
                int minIdx = LargestAddrs.IndexOf(LargestAddrs.MinBy(x => x.Size)!);
                LargestAddrs[minIdx] = (addr, size, TypeName);
            }
        }
    }

    private static bool HasIDisposable(ClrType type)
    {
        for (var t = type; t != null; t = t.BaseType)
            if (t.Name == "System.IDisposable")
                return true;
        return false;
    }

    private static string GetGenLabel(DumpContext ctx, ulong addr)
    {
        var seg = ctx.Heap.GetSegmentByAddress(addr);
        if (seg is null) return "?";
        return seg.Kind switch
        {
            GCSegmentKind.Large    => "LOH",
            GCSegmentKind.Pinned   => "POH",
            GCSegmentKind.Frozen   => "Frozen",
            GCSegmentKind.Ephemeral => EphemeralGen(seg, addr),
            _                      => "Gen2",
        };
    }

    private static string EphemeralGen(ClrSegment seg, ulong addr)
    {
        if (seg.Generation0.Contains(addr)) return "Gen0";
        if (seg.Generation1.Contains(addr)) return "Gen1";
        return "Gen2";
    }

    private static bool GenMatches(string gen, string filter) => filter switch
    {
        "0"   => gen == "Gen0",
        "1"   => gen == "Gen1",
        "2"   => gen == "Gen2",
        "loh" => gen == "LOH",
        "poh" => gen == "POH",
        _     => true,
    };

    private static int GenSortKey(string gen) => gen switch
    {
        "Gen0" => 0, "Gen1" => 1, "Gen2" => 2, "LOH" => 3, "POH" => 4, _ => 5
    };
}
