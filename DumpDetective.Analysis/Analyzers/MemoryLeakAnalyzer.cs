using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;

namespace DumpDetective.Analysis.Analyzers;

public sealed class MemoryLeakAnalyzer
{
    public MemoryLeakData Analyze(DumpContext ctx,
        int top = 30, int minCount = 500,
        bool noRootTrace = false, bool includeSystem = false)
    {
        // Step 1 + 2: heap walk for type stats
        var typeStats = new Dictionary<string, (long Count, long Size, string Gen, ulong SampleAddr)>(
            StringComparer.Ordinal);

        CommandBase.RunStatus("Walking heap (Step 1: dumpheap-stat)...", () =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                string name = obj.Type.Name ?? "<unknown>";
                long   size = (long)obj.Size;

                ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(typeStats, name, out bool existed);
                if (existed)
                    entry = (entry.Count + 1, entry.Size + size, entry.Gen, entry.SampleAddr);
                else
                {
                    var seg = ctx.Heap.GetSegmentByAddress(obj.Address);
                    string gen = seg?.Kind switch
                    {
                        GCSegmentKind.Large => "LOH", GCSegmentKind.Pinned => "POH",
                        _ => "Gen2",
                    };
                    entry = (1, size, gen, obj.Address);
                }
            }
        });

        var allTypes = typeStats
            .Select(kv => new HeapStatRow(kv.Key, kv.Value.Count, kv.Value.Size, kv.Value.Gen))
            .OrderByDescending(r => r.Size)
            .ToList();

        long totalHeapSize    = allTypes.Sum(r => r.Size);

        // Filter for app suspects
        var suspects = allTypes
            .Where(r => (includeSystem || !DumpHelpers.IsSystemType(r.Name)) && r.Count >= minCount)
            .Take(top)
            .ToList();

        // Step 3: string stats
        long totalStrSize  = 0;
        long totalStrCount = 0;
        if (typeStats.TryGetValue("System.String", out var strStats))
        {
            totalStrSize  = strStats.Size;
            totalStrCount = strStats.Count;
        }

        // Step 4: root chains for suspects (optional)
        var rootChains = new List<MemoryRootChain>();
        if (!noRootTrace && suspects.Count > 0)
        {
            CommandBase.RunStatus("Tracing GC roots (Step 4)...", () =>
            {
                foreach (var suspect in suspects.Take(10))
                {
                    if (typeStats.TryGetValue(suspect.Name, out var ts) && ts.SampleAddr != 0)
                    {
                        var chain = TraceRootChain(ctx, ts.SampleAddr);
                        if (chain.Count > 0)
                            rootChains.Add(new MemoryRootChain(suspect.Name, ts.SampleAddr, chain));
                    }
                }
            });
        }

        return new MemoryLeakData(allTypes.Take(top * 2).ToList(), suspects,
            totalHeapSize, totalStrSize, totalStrCount, rootChains);
    }

    // BFS from the target object upward through references to find a GC root.
    // Returns the chain from root → target, or empty if no root found within budget.
    private static List<string> TraceRootChain(DumpContext ctx, ulong targetAddr)
    {
        try
        {
            // Build an inbound reference map up to our budget
            const int Budget = 100_000;
            var inbound = new Dictionary<ulong, ulong>();  // child → parent
            int seen    = 0;

            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                if (seen++ > Budget) break;
                try
                {
                    foreach (var child in obj.EnumerateReferences(carefully: false))
                        if (!inbound.ContainsKey(child.Address))
                            inbound[child.Address] = obj.Address;
                }
                catch { }
            }

            // Walk inbound map from target back to a root
            var chain  = new List<string>();
            var visited = new HashSet<ulong>();
            ulong cur = targetAddr;
            while (inbound.TryGetValue(cur, out ulong parent) && visited.Add(cur))
            {
                var obj = ctx.Heap.GetObject(parent);
                chain.Add(obj.Type?.Name ?? $"0x{parent:X16}");
                cur = parent;
            }
            chain.Reverse();
            return chain;
        }
        catch { return []; }
    }
}
