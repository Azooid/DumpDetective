using DumpDetective.Analysis.Memory.Analyzers;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Memory;

/// <summary>
/// Public façade used by <c>LoadCommand</c> to trigger internal cache-building
/// operations without exposing the internal implementation types.
/// </summary>
public static class LoadHelper
{
    /// <summary>
    /// Builds (or loads from cache) the parent map and hot-addr-types for the dump.
    /// Both results are persisted to disk.  This is the most expensive step in
    /// <c>load</c> — if the BFS index is available it avoids a heap walk entirely.
    /// </summary>
    public static void BuildReferrerCache(DumpContext ctx, Action<string>? progress = null)
        => ctx.GetOrCreateAnalysis<SharedReferrerCache>(() => SharedReferrerCache.Build(ctx, progress));

    /// <summary>
    /// Builds (or loads from cache) the static-roots address set and persists it to
    /// <c>static-roots.bin</c>. Returns the address count and skipped module count.
    /// </summary>
    public static (int AddressCount, int SkippedModules) BuildStaticRootsCache(DumpContext ctx)
    {
        var result = StaticRootAddresses.Build(ctx);
        return (result.Addresses.Count, result.SkippedModules);
    }
}
