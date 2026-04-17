using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;
using System.Diagnostics;

namespace DumpDetective.Commands;

// Finds all instances of a given type name (case-insensitive substring match).
// Reports counts, sizes, generation distribution, and largest individual instances.
// Optionally displays all object addresses.
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
          -o, --output <f>     Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        string? typeName = null;
        int top = 50;
        bool showAddr = false;
        long minSize = 0;
        string? genFilter = null;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--type" or "-t") && i + 1 < args.Length)
                typeName = args[++i];
            else if ((args[i] is "--top" or "-n") && i + 1 < args.Length)
                int.TryParse(args[++i], out top);
            else if (args[i] is "--addresses" or "-a")
                showAddr = true;
            else if (args[i] == "--min-size" && i + 1 < args.Length)
                long.TryParse(args[++i], out minSize);
            else if (args[i] == "--gen" && i + 1 < args.Length)
                genFilter = args[++i].ToLowerInvariant();
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

        var typeStats = ScanInstances(ctx, typeName, top, minSize, genFilter);

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

        RenderTypeSummary(sink, typeStats, totalCount, totalSize);
        RenderGenBreakdown(sink, typeStats);
        RenderLargestInstances(sink, typeStats);
        RenderPerAddress(sink, typeStats, top, showAddr);

        sink.KeyValues([
            ("Total instances", totalCount.ToString("N0")),
            ("Total size",      DumpHelpers.FormatSize(totalSize)),
            ("Distinct types",  typeStats.Count.ToString("N0")),
        ]);
    }

    // Walks the heap finding all objects whose type name contains typeName (case-insensitive).
    // Applies minSize and genFilter constraints. Returns a per-exact-typename TypeData map.
    static Dictionary<string, TypeData> ScanInstances(
        DumpContext ctx, string typeName, int top, long minSize, string? genFilter)
    {
        // Type → (Count, TotalSize, Gen0c, Gen1c, Gen2c, LOHc, MaxSingle, list of top-N largest)
        var typeStats    = new Dictionary<string, TypeData>(StringComparer.Ordinal);
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

        return typeStats;
    }

    // Renders the type-summary table: one row per exact type name, sorted by total size.
    static void RenderTypeSummary(IRenderSink sink,
        Dictionary<string, TypeData> typeStats, long totalCount, long totalSize)
    {
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
    }

    // Renders the generation-distribution breakdown across all matched types.
    static void RenderGenBreakdown(IRenderSink sink, Dictionary<string, TypeData> typeStats)
    {
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
    }

    // Renders the top-10 largest individual instances across all matched types.
    static void RenderLargestInstances(IRenderSink sink, Dictionary<string, TypeData> typeStats)
    {
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
    }

    // Renders per-type object-address accordions when --addresses is set.
    static void RenderPerAddress(IRenderSink sink,
        Dictionary<string, TypeData> typeStats, int top, bool showAddr)
    {
        if (!showAddr) return;

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

    // Returns true if the type or any of its base types is named System.IDisposable.
    // Used to flag instances that should be tracked for disposal.
    private static bool HasIDisposable(ClrType type)
    {
        for (var t = type; t != null; t = t.BaseType)
            if (t.Name == "System.IDisposable")
                return true;
        return false;
    }

    // Maps an object address to a generation label (LOH / POH / Frozen / Gen0-2 / ?).
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

    // Determines Gen0/1/2 for an address inside an ephemeral segment.
    private static string EphemeralGen(ClrSegment seg, ulong addr)
    {
        if (seg.Generation0.Contains(addr)) return "Gen0";
        if (seg.Generation1.Contains(addr)) return "Gen1";
        return "Gen2";
    }

    // Returns true when the gen label matches the --gen CLI filter value.
    private static bool GenMatches(string gen, string filter) => filter switch
    {
        "0"   => gen == "Gen0",
        "1"   => gen == "Gen1",
        "2"   => gen == "Gen2",
        "loh" => gen == "LOH",
        "poh" => gen == "POH",
        _     => true,
    };

    // Numeric sort key for generation rows: Gen0 < Gen1 < Gen2 < LOH < POH < other.
    private static int GenSortKey(string gen) => gen switch
    {
        "Gen0" => 0, "Gen1" => 1, "Gen2" => 2, "LOH" => 3, "POH" => 4, _ => 5
    };
}
