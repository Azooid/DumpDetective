using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

public sealed class TypeInstancesAnalyzer
{
    public TypeInstancesData Analyze(DumpContext ctx, string typeName,
        int top = 50, long minSize = 0, string? genFilter = null)
    {
        var typeMap = new Dictionary<string, (long Count, long TotalSize, int G0, int G1, int G2, int Loh, long MaxSingle, List<InstanceEntry> Largest)>(StringComparer.Ordinal);

        CommandBase.RunStatus($"Scanning for '{typeName}'...", () =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                string name = obj.Type.Name ?? "";
                if (!name.Contains(typeName, StringComparison.OrdinalIgnoreCase)) continue;

                long size = (long)obj.Size;
                if (size < minSize) continue;

                string gen = GetGen(ctx.Heap, obj.Address);
                if (genFilter is not null && !GenMatches(gen, genFilter)) continue;

                if (!typeMap.TryGetValue(name, out var e))
                    e = (0, 0, 0, 0, 0, 0, 0, new List<InstanceEntry>());

                e = (
                    Count:      e.Count + 1,
                    TotalSize:  e.TotalSize + size,
                    G0:         e.G0  + (gen == "Gen0" ? 1 : 0),
                    G1:         e.G1  + (gen == "Gen1" ? 1 : 0),
                    G2:         e.G2  + (gen == "Gen2" ? 1 : 0),
                    Loh:        e.Loh + (gen == "LOH"  ? 1 : 0),
                    MaxSingle:  Math.Max(e.MaxSingle, size),
                    Largest:    e.Largest
                );

                if (e.Largest.Count < top || size > (e.Largest.Count > 0 ? e.Largest.Min(x => x.Size) : 0))
                {
                    e.Largest.Add(new InstanceEntry(obj.Address, size, gen));
                    if (e.Largest.Count > top)
                        e.Largest.RemoveAt(e.Largest.FindIndex(x => x.Size == e.Largest.Min(l => l.Size)));
                }

                typeMap[name] = e;
            }
        });

        var result = new Dictionary<string, TypeMatchStats>(typeMap.Count, StringComparer.Ordinal);
        long totalCount = 0, totalSize = 0;

        foreach (var (name, e) in typeMap)
        {
            e.Largest.Sort((a, b) => b.Size.CompareTo(a.Size));
            result[name] = new TypeMatchStats(e.Count, e.TotalSize, e.G0, e.G1, e.G2, e.Loh, e.MaxSingle, e.Largest);
            totalCount += e.Count;
            totalSize  += e.TotalSize;
        }

        return new TypeInstancesData(result, totalCount, totalSize, typeName);
    }

    private static string GetGen(ClrHeap heap, ulong addr)
    {
        var seg = heap.GetSegmentByAddress(addr);
        return seg?.Kind switch
        {
            GCSegmentKind.Large  => "LOH",
            GCSegmentKind.Pinned => "POH",
            GCSegmentKind.Ephemeral =>
                seg.Generation0.Contains(addr) ? "Gen0" :
                seg.Generation1.Contains(addr) ? "Gen1" : "Gen2",
            _ => "Gen2",
        };
    }

    private static bool GenMatches(string gen, string filter) => filter switch
    {
        "0"   => gen == "Gen0",
        "1"   => gen == "Gen1",
        "2"   => gen == "Gen2",
        "loh" => gen == "LOH",
        _     => true,
    };
}
