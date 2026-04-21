using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

public sealed class HeapStatsAnalyzer
{
    /// <summary>
    /// Returns per-type stats, using the cached <see cref="HeapSnapshot"/> fast-path
    /// when no filters are active.
    /// </summary>
    public HeapStatsData Analyze(DumpContext ctx, string? filter = null, string? genFilter = null)
    {
        // Fast path — reuse HeapSnapshot when no object-level filters are active
        if (ctx.Snapshot is { } snap && filter is null && genFilter is null)
        {
            var rows = new List<HeapStatRow>(snap.TypeStats.Count);
            foreach (var (name, a) in snap.TypeStats)
                rows.Add(new HeapStatRow(name, a.Count, a.Size, a.GenLabel, a.MT));
            return new HeapStatsData(rows, rows.Sum(r => r.Size), rows.Sum(r => r.Count));
        }

        // Slow path — own heap walk with optional filters
        var stats = new Dictionary<string, (long Count, long Size, string Gen)>(StringComparer.Ordinal);
        var mtToGen = new Dictionary<ulong, string>();

        CommandBase.RunStatus("Walking heap...", () =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;

                if (!mtToGen.TryGetValue(obj.Type.MethodTable, out var gen))
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

                if (genFilter is not null && !GenMatches(gen, genFilter)) continue;
                var name = obj.Type.Name ?? "<unknown>";
                if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

                long size = (long)obj.Size;
                if (stats.TryGetValue(name, out var e)) stats[name] = (e.Count + 1, e.Size + size, e.Gen);
                else                                    stats[name] = (1, size, gen);
            }
        });

        var result = new List<HeapStatRow>(stats.Count);
        long total = 0, objs = 0;
        foreach (var kv in stats)
        {
            result.Add(new HeapStatRow(kv.Key, kv.Value.Count, kv.Value.Size, kv.Value.Gen));
            total += kv.Value.Size;
            objs  += kv.Value.Count;
        }
        return new HeapStatsData(result, total, objs);
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
}
