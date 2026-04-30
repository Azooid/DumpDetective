using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Memory.Analyzers;

/// <summary>
/// Lists individual instances of types whose name contains a search substring,
/// with per-instance address, size, and generation.
/// Walks the full heap (no fast path — needs individual addresses and sizes, not just
/// aggregated counts from <c>TypeAgg</c>). Applies optional minimum-size and
/// generation filters inline during the walk. Tracks the <c>top</c> largest instances
/// per matching type using a maintained-minimum list capped at <c>top</c> entries
/// (O(1) guard check; O(top) eviction — no quadratic LINQ Min scans).
/// </summary>
public sealed class TypeInstancesAnalyzer
{
    // Mutable per-type accumulator — avoids repeated value-type copy-and-reassign
    // on every object hit (the old tuple approach copied all fields each iteration).
    private sealed class TypeEntry
    {
        public long Count;
        public long TotalSize;
        public int G0, G1, G2, Loh, Poh;
        public long MaxSingle;
        public long MinLargest;   // maintained minimum of Largest list — O(1) guard
        public readonly List<InstanceEntry> Largest = [];

        /// <summary>
        /// Adds <paramref name="entry"/> if it belongs in the top-<paramref name="top"/> list.
        /// Guard check is O(1). Eviction is one linear scan O(top) — only triggered when
        /// the list is full and the new entry is larger than the current minimum.
        /// </summary>
        public void TryAddLargest(InstanceEntry entry, int top)
        {
            if (Largest.Count < top)
            {
                Largest.Add(entry);
                if (Largest.Count == 1 || entry.Size < MinLargest)
                    MinLargest = entry.Size;
            }
            else if (entry.Size > MinLargest)
            {
                // Replace the smallest entry in one linear scan.
                int minIdx = 0;
                for (int j = 1; j < Largest.Count; j++)
                    if (Largest[j].Size < Largest[minIdx].Size) minIdx = j;
                Largest[minIdx] = entry;
                // Recompute minimum (O(top), rare path).
                MinLargest = Largest[0].Size;
                for (int j = 1; j < Largest.Count; j++)
                    if (Largest[j].Size < MinLargest) MinLargest = Largest[j].Size;
            }
        }
    }

    public TypeInstancesData Analyze(DumpContext ctx, string typeName,
        int top = 50, long minSize = 0, string? genFilter = null)
    {
        // Load BFS cache once — used to compute retained size for each matched instance.
        BfsIndexCache? bfsCache = null;
        if (BfsIndexCache.IsValid(BfsIndexCache.CachePath(ctx.DumpPath), ctx.DumpPath))
        {
            CommandBase.RunStatus("Loading BFS index...", update =>
                bfsCache = ctx.GetOrCreateAnalysis<BfsCacheBox>(() =>
                    new BfsCacheBox(BfsIndexCache.TryLoad(ctx.DumpPath, update))).Cache);
        }

        var typeMap = new Dictionary<string, TypeEntry>(StringComparer.Ordinal);

        CommandBase.RunStatus($"Scanning for '{typeName}'...", update =>
        {
            long count = 0;
            long hits  = 0;
            var  sw    = System.Diagnostics.Stopwatch.StartNew();
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                count++;
                if ((count & 0x3FFF) == 0 && sw.ElapsedMilliseconds >= 200)
                {
                    update($"Scanning for '{typeName}' \u2014 {count:N0} objects  \u2022  {hits} matches...");
                    sw.Restart();
                }
                string name = obj.Type.Name ?? "";
                if (!name.Contains(typeName, StringComparison.OrdinalIgnoreCase)) continue;
                hits++;

                long size = (long)obj.Size;
                if (size < minSize) continue;

                string gen = GetGen(ctx.Heap, obj.Address);
                if (genFilter is not null && !GenMatches(gen, genFilter)) continue;

                if (!typeMap.TryGetValue(name, out var e))
                    typeMap[name] = e = new TypeEntry();

                e.Count++;
                e.TotalSize += size;
                if      (gen == "Gen0") e.G0++;
                else if (gen == "Gen1") e.G1++;
                else if (gen == "Gen2") e.G2++;
                else if (gen == "LOH")  e.Loh++;
                else if (gen == "POH")  e.Poh++;
                if (size > e.MaxSingle) e.MaxSingle = size;
                e.TryAddLargest(new InstanceEntry(obj.Address, size, gen), top);
            }
        });

        var result = new Dictionary<string, TypeMatchStats>(typeMap.Count, StringComparer.Ordinal);
        long totalCount = 0, totalSize = 0;

        // If BFS cache available, compute retained for each top-N instance.
        if (bfsCache is not null && typeMap.Count > 0)
        {
            CommandBase.RunStatus("Computing retained sizes (BFS)...", update =>
            {
                int done2 = 0;
                foreach (var e in typeMap.Values)
                {
                    for (int j = 0; j < e.Largest.Count; j++)
                    {
                        var inst = e.Largest[j];
                        var (ret, _) = bfsCache.ComputeRetained(inst.Addr, new HashSet<int>());
                        e.Largest[j] = inst with { RetainedSize = ret };
                    }
                    done2++;
                    if ((done2 & 0x3F) == 0)
                        update($"Computing retained \u2014 {done2}/{typeMap.Count} types...");
                }
            });
        }

        foreach (var (name, e) in typeMap)
        {
            e.Largest.Sort((a, b) => b.Size.CompareTo(a.Size));
            long totalRetained = e.Largest.Sum(i => i.RetainedSize);
            result[name] = new TypeMatchStats(e.Count, e.TotalSize, e.G0, e.G1, e.G2, e.Loh, e.Poh, e.MaxSingle, e.Largest, totalRetained);
            totalCount += e.Count;
            totalSize  += e.TotalSize;
        }

        return new TypeInstancesData(result, totalCount, totalSize, typeName, HasRetained: bfsCache is not null);
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
