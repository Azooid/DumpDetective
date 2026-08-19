using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Memory.Analyzers;

/// <summary>
/// Converts a <see cref="DomTreeCache"/> into a <see cref="DominatorTreeData"/> suitable
/// for rendering.  Objects are grouped by type at each level of the dominator tree, then
/// sorted by retained size descending.  Each level is capped at <paramref name="topN"/>
/// entries to keep output readable.
/// </summary>
public sealed class DomTreeAnalyzer
{
    public DominatorTreeData Analyze(
        DumpContext   ctx,
        DomTreeCache  cache,
        BfsIndexCache bfsCache,
        int           topN     = 20,
        int           maxDepth = 8,
        long          minBytes = 1L << 20) // 1 MB floor
    {
        int N = cache.NodeCount;
        int virtualRoot = N; // sentinel value used in idom[]

        // ── Build dominator-tree children index (CSR, O(N)) ──────────────────
        // Each reachable non-root node contributes exactly one child entry.

        var childOff = new int[N + 2]; // +2: [N] = virtualRoot, [N+1] = sentinel end

        for (int v = 0; v < N; v++)
        {
            int d = cache.Idom[v];
            if (d < 0) continue;            // unreachable
            int bucket = d == virtualRoot ? N : d;
            childOff[bucket + 1]++;
        }
        // Prefix sum
        for (int i = 0; i < N + 1; i++)
            childOff[i + 1] += childOff[i];

        var childArr  = new int[childOff[N + 1]]; // total = reachable nodes
        var fillPos   = new int[N + 1];
        for (int v = 0; v < N; v++)
        {
            int d = cache.Idom[v];
            if (d < 0) continue;
            int bucket = d == virtualRoot ? N : d;
            childArr[childOff[bucket] + fillPos[bucket]++] = v;
        }
        fillPos = null!;

        // ── Type-name lookup (lazy via ClrMD, cached by address) ──────────────
        var typeCache = new Dictionary<ulong, string>(4096);
        ClrHeap heap  = ctx.Runtime.Heap;

        string GetTypeName(int idx)
        {
            ulong addr = bfsCache.IndexToAddr[idx];
            if (!typeCache.TryGetValue(addr, out string? name))
            {
                name = heap.GetObjectType(addr)?.Name ?? "<unknown>";
                typeCache[addr] = name;
            }
            return name;
        }

        // ── Recursive tree builder ────────────────────────────────────────────

        DomRetainerNode[] BuildLevel(int parentBucket, int depth)
        {
            int start = childOff[parentBucket];
            int end   = childOff[parentBucket + 1];
            if (start == end || depth > maxDepth) return [];

            // Group siblings by type, aggregate sizes
            var byType = new Dictionary<string, (long shallow, long retained, int count)>(StringComparer.Ordinal);

            for (int ci = start; ci < end; ci++)
            {
                int child = childArr[ci];
                if (cache.Retained[child] < minBytes) continue;

                string typeName = GetTypeName(child);
                if (!byType.TryGetValue(typeName, out var agg))
                    agg = (0, 0, 0);
                byType[typeName] = (
                    agg.shallow  + bfsCache.Sizes[child],
                    agg.retained + cache.Retained[child],
                    agg.count    + 1);
            }

            if (byType.Count == 0) return [];

            var sorted = byType
                .OrderByDescending(kv => kv.Value.retained)
                .Take(topN)
                .ToArray();

            long totalHeapForPct = bfsCache.Sizes.Sum(); // same for all calls; cached implicitly

            var nodes = new DomRetainerNode[sorted.Length];
            for (int i = 0; i < sorted.Length; i++)
            {
                var (typeName, (shallow, retained, count)) = sorted[i];

                // Representative: object of this type with the highest retained size
                int  repIdx = -1;
                long repMax = -1;
                for (int ci = start; ci < end; ci++)
                {
                    int child = childArr[ci];
                    if (GetTypeName(child) == typeName && cache.Retained[child] > repMax)
                    {
                        repMax = cache.Retained[child]; repIdx = child;
                    }
                }

                DomRetainerNode[] children = repIdx >= 0
                    ? BuildLevel(repIdx, depth + 1)
                    : [];

                double pct = totalHeapForPct > 0 ? retained * 100.0 / totalHeapForPct : 0;
                nodes[i] = new DomRetainerNode(typeName, count, shallow, retained, pct, children);
            }

            return nodes;
        }

        // ── Top-level: children of virtual root ───────────────────────────────

        var roots = BuildLevel(N, 1);

        long totalHeap       = bfsCache.Sizes.Sum();
        long totalReachable  = 0;
        for (int v = 0; v < N; v++)
            if (cache.Idom[v] >= 0) totalReachable++;

        bool truncated = roots.Length == topN;

        return new DominatorTreeData(roots, totalHeap, totalReachable, maxDepth, truncated);
    }
}
