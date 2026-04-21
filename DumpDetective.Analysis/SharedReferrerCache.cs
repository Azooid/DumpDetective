using DumpDetective.Core.Runtime;
using Microsoft.Diagnostics.Runtime;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Built once per <see cref="DumpContext"/> in a single heap walk and shared between
/// <c>MemoryLeakAnalyzer</c> (BFS root tracing) and <c>HighRefsAnalyzer</c> (referencing type counts).
/// Stored via <see cref="DumpContext.GetOrCreateAnalysis{T}"/> — whichever of the two analyzers
/// runs first builds it; the second receives the already-built result instantly.
/// </summary>
internal sealed class SharedReferrerCache
{
    /// <summary>Max parent entries stored per child address for BFS root tracing.</summary>
    internal const int MaxBfsParents = 8;

    /// <summary>
    /// For memory-leak BFS chains: child address → up to <see cref="MaxBfsParents"/> (parentAddr, parentType).
    /// </summary>
    public readonly Dictionary<ulong, List<(ulong ParentAddr, string ParentType)>> BfsMap;

    /// <summary>
    /// For high-refs display: hot address → (referencing type name → count).
    /// Only populated for addresses that were in the top-N inbound-ref list at build time.
    /// Accumulation is unbounded so type distributions are accurate.
    /// </summary>
    public readonly Dictionary<ulong, Dictionary<string, int>> HotAddrTypes;

    private SharedReferrerCache(
        Dictionary<ulong, List<(ulong, string)>> bfsMap,
        Dictionary<ulong, Dictionary<string, int>> hotAddrTypes)
    {
        BfsMap       = bfsMap;
        HotAddrTypes = hotAddrTypes;
    }

    /// <summary>
    /// Builds the cache with a single <c>EnumerateObjects</c> pass.
    /// Hot addresses are derived from the snapshot's inbound-count map using the same
    /// defaults as <c>HighRefsAnalyzer</c> (top=30, minRefs=10).
    /// When <paramref name="progress"/> is provided, emits live spinner updates and a
    /// final <c>[SCAN]Referrer map|count|ms</c> line compatible with <c>ProgressLogger</c>.
    /// </summary>
    internal static SharedReferrerCache Build(DumpContext ctx, Action<string>? progress = null)
    {
        // Compute hot addresses from the snapshot (instant — already in memory).
        var hotAddrs = new HashSet<ulong>();
        if (ctx.Snapshot is { } snap)
        {
            foreach (var kv in snap.InboundCounts
                .Where(kv => kv.Value >= 10)
                .OrderByDescending(kv => kv.Value)
                .Take(30))
                hotAddrs.Add(kv.Key);
        }

        var bfsMap   = new Dictionary<ulong, List<(ulong, string)>>(256_000);
        var hotTypes = new Dictionary<ulong, Dictionary<string, int>>(hotAddrs.Count);
        foreach (var a in hotAddrs)
            hotTypes[a] = new Dictionary<string, int>(32, StringComparer.Ordinal);

        long processedCount = 0;
        var  totalWatch     = progress is not null ? Stopwatch.StartNew() : null;
        var  rateWatch      = progress is not null ? Stopwatch.StartNew() : null;
        long lastCount      = 0;

        foreach (var obj in ctx.Heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
            string pType = obj.Type.Name ?? "?";
            ulong  pAddr = obj.Address;

            if (progress is not null)
            {
                processedCount++;
                if ((processedCount & 0x3FF) == 0 && rateWatch!.ElapsedMilliseconds >= 200)
                {
                    double elapsed  = totalWatch!.Elapsed.TotalSeconds;
                    double interval = rateWatch.Elapsed.TotalSeconds;
                    long   delta    = processedCount - lastCount;
                    long   rate     = interval > 0 ? (long)(delta / interval) : 0;
                    lastCount = processedCount;
                    rateWatch.Restart();
                    progress($"Walking heap objects — {processedCount:N0} objs  •  {elapsed:F1}s  •  ~{rate:N0}/s");
                }
            }

            try
            {
                foreach (var refAddr in obj.EnumerateReferenceAddresses(carefully: false))
                {
                    if (refAddr == 0 || refAddr == pAddr) continue;

                    // BFS map — bounded by MaxBfsParents
                    if (bfsMap.TryGetValue(refAddr, out var existing))
                    {
                        if (existing.Count < MaxBfsParents)
                            existing.Add((pAddr, pType));
                    }
                    else
                    {
                        bfsMap[refAddr] = new List<(ulong, string)>(2) { (pAddr, pType) };
                    }

                    // Hot-addr type counts — unbounded per hot address
                    if (hotTypes.TryGetValue(refAddr, out var typeMap))
                    {
                        ref int c = ref CollectionsMarshal.GetValueRefOrAddDefault(typeMap, pType, out _);
                        c++;
                    }
                }
            }
            catch { }
        }

        if (progress is not null && totalWatch is not null)
            progress($"[SCAN]Referrer map|{processedCount}|{(long)totalWatch.Elapsed.TotalMilliseconds}");

        return new SharedReferrerCache(bfsMap, hotTypes);
    }
}
