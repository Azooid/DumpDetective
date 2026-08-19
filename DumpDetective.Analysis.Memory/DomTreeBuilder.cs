using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Memory;

/// <summary>
/// Builds a <see cref="DomTreeCache"/> for a dump by running the Lengauer-Tarjan
/// dominator algorithm over the heap reference graph stored in a <see cref="BfsIndexCache"/>.
///
/// <para>
/// Build pipeline:
/// <list type="number">
///   <item>Load BFS index cache (forward CSR: all object references).</item>
///   <item>Collect GC root addresses via ClrMD (or from cached gc-roots.bin).</item>
///   <item>Augment the forward CSR with a virtual root (index N) connected to all GC roots.</item>
///   <item>Build a reverse CSR (predecessors per node) from the augmented forward CSR.</item>
///   <item>Run <see cref="LengauerTarjan.Compute"/> to get <c>idom[]</c>.</item>
///   <item>Compute dominator-subtree retained sizes bottom-up (reverse DFS order).</item>
///   <item>Write <see cref="DomTreeCache"/> to <c>.idom.idx</c>.</item>
/// </list>
/// </para>
///
/// <para>
/// Peak additional memory (beyond the already-loaded BfsIndexCache):
/// reverse-CSR (≈E×4 bytes) + LT arrays (≈N×36 bytes) for N objects and E edges.
/// At 10M objects / 30M edges: ~700 MB.  At 80M / 240M: ~5 GB.
/// </para>
/// </summary>
public static class DomTreeBuilder
{
    /// <summary>Intermediate state after GC-root derivation + augmented-graph build (pass 1).</summary>
    public sealed class Pass1State
    {
        internal readonly int   AugN;
        internal readonly int   AugM;
        internal readonly int[] AugFwdOff;
        internal readonly int[] AugFwdEdge;
        internal readonly int[] AugRevOff;
        internal readonly int[] AugRevEdge;
        public   readonly int   NodeCount;      // BFS node count (excluding virtual root)
        public   readonly int   GcRootCount;

        internal Pass1State(int augN, int augM,
            int[] augFwdOff, int[] augFwdEdge,
            int[] augRevOff, int[] augRevEdge,
            int nodeCount, int gcRootCount)
        {
            AugN = augN; AugM = augM;
            AugFwdOff = augFwdOff; AugFwdEdge = augFwdEdge;
            AugRevOff = augRevOff; AugRevEdge = augRevEdge;
            NodeCount = nodeCount; GcRootCount = gcRootCount;
        }
    }

    /// <summary>Intermediate state after Lengauer-Tarjan (pass 2).</summary>
    public sealed class Pass2State
    {
        internal readonly LengauerTarjan.Result LtResult;
        public   readonly int                   NodeCount;
        internal Pass2State(LengauerTarjan.Result lt, int nodeCount)
            { LtResult = lt; NodeCount = nodeCount; }
    }
    /// <summary>
    /// Loads or builds a <see cref="DomTreeCache"/> for the given <paramref name="ctx"/>.
    /// BFS index is resolved via <c>ctx.GetOrCreateAnalysis&lt;BfsCacheBox&gt;</c> so it is
    /// shared with other parallel commands that already loaded it.
    /// Returns <see langword="null"/> when no BFS index exists for this dump.
    /// </summary>
    public static DomTreeCache? LoadOrBuild(
        DumpContext     ctx,
        bool            forceRebuild = false,
        Action<string>? update       = null)
    {
        string cachePath = DomTreeCache.CachePath(ctx.DumpPath);

        if (!forceRebuild && DomTreeCache.IsValid(cachePath, ctx.DumpPath))
            return DomTreeCache.TryLoad(ctx.DumpPath, update);

        // Resolve BFS cache — returns instantly if AnalyzeCommand pre-loaded it
        var bfsCache = ctx.GetOrCreateAnalysis<BfsCacheBox>(() =>
            new BfsCacheBox(BfsIndexCache.TryLoad(ctx.DumpPath, update))).Cache;

        if (bfsCache is null)
        {
            update?.Invoke("No BFS index found — skipping dominator tree build.");
            return null;
        }

        return Build(ctx, bfsCache, cachePath, update);
    }

    // Overload retained for callers (e.g. LoadCommand) that already have the BFS cache in hand.
    public static DomTreeCache? LoadOrBuild(
        DumpContext     ctx,
        BfsIndexCache   bfsCache,
        bool            forceRebuild = false,
        Action<string>? update       = null)
    {
        string cachePath = DomTreeCache.CachePath(ctx.DumpPath);
        if (!forceRebuild && DomTreeCache.IsValid(cachePath, ctx.DumpPath))
            return DomTreeCache.TryLoad(ctx.DumpPath, update);
        return Build(ctx, bfsCache, cachePath, update);
    }

    private static DomTreeCache Build(
        DumpContext     ctx,
        BfsIndexCache   bfsCache,
        string          cachePath,
        Action<string>? update)
    {
        var p1 = BuildGraph(ctx, bfsCache, update);
        var p2 = RunLT(p1, update);
        return FinalizeAndSave(p2, bfsCache, cachePath, ctx.DumpPath, update);
    }

    /// <summary>Pass 1: derive GC roots + build augmented forward/reverse CSR.</summary>
    public static Pass1State BuildGraph(
        DumpContext     ctx,
        BfsIndexCache   bfsCache,
        Action<string>? update = null)
    {
        int N = bfsCache.NodeCount;
        int M = bfsCache.EdgeCount;

        update?.Invoke("Collecting GC roots…");
        var gcRootIndices = CollectGcRootIndices(ctx, bfsCache, update);
        int rootCount = gcRootIndices.Count;

        update?.Invoke("Building augmented graph…");

        int augN = N + 1;
        int augM = M + rootCount;

        var augFwdOff  = new int[augN + 1];
        var augFwdEdge = new int[augM];

        bfsCache.Offsets.AsSpan(0, N + 1).CopyTo(augFwdOff.AsSpan(0, N + 1));
        bfsCache.Children.AsSpan(0, M).CopyTo(augFwdEdge.AsSpan(0, M));

        augFwdOff[N]     = M;
        augFwdOff[N + 1] = M + rootCount;
        for (int i = 0; i < rootCount; i++)
            augFwdEdge[M + i] = gcRootIndices[i];

        update?.Invoke("Building reverse graph…");

        var augRevOff  = new int[augN + 1];
        var fill       = new int[augN];

        for (int e = 0; e < augM; e++)
            augRevOff[augFwdEdge[e] + 1]++;
        for (int i = 0; i < augN; i++)
            augRevOff[i + 1] += augRevOff[i];

        var augRevEdge = new int[augM];
        for (int u = 0; u < augN; u++)
        {
            int start = augFwdOff[u];
            int end   = augFwdOff[u + 1];
            for (int e = start; e < end; e++)
            {
                int v = augFwdEdge[e];
                augRevEdge[augRevOff[v] + fill[v]++] = u;
            }
        }

        return new Pass1State(augN, augM, augFwdOff, augFwdEdge, augRevOff, augRevEdge, N, rootCount);
    }

    /// <summary>Pass 2: run Lengauer-Tarjan over the augmented graph.</summary>
    public static Pass2State RunLT(Pass1State p1, Action<string>? update = null)
    {
        update?.Invoke("Running Lengauer-Tarjan dominator algorithm…");

        var ltResult = LengauerTarjan.Compute(
            p1.AugN,
            p1.AugFwdOff.AsSpan(),
            p1.AugFwdEdge.AsSpan(),
            p1.AugRevOff.AsSpan(),
            p1.AugRevEdge.AsSpan(),
            root: p1.NodeCount);

        return new Pass2State(ltResult, p1.NodeCount);
    }

    /// <summary>Pass 3: compute retained sizes bottom-up and save to disk.</summary>
    public static DomTreeCache FinalizeAndSave(
        Pass2State      p2,
        BfsIndexCache   bfsCache,
        string          cachePath,
        string          dumpPath,
        Action<string>? update = null)
    {
        int N      = p2.NodeCount;
        int augN   = N + 1;
        int dfsCnt = p2.LtResult.ReachableCount;

        update?.Invoke("Computing retained sizes…");

        var retained = new long[augN];
        for (int i = 0; i < N; i++)
            retained[i] = bfsCache.Sizes[i];

        for (int i = dfsCnt - 1; i >= 1; i--)
        {
            int w = p2.LtResult.Vertex[i];
            int d = p2.LtResult.Idom[w];
            if (d >= 0 && d != w)
                retained[d] += retained[w];
        }

        var outIdom     = p2.LtResult.Idom.AsSpan(0, N).ToArray();
        var outRetained = retained.AsSpan(0, N).ToArray();

        var cache = new DomTreeCache(N, outIdom, outRetained);
        cache.Save(cachePath, dumpPath, update);
        return cache;
    }

    // ── GC root collection ────────────────────────────────────────────────────

    private static List<int> CollectGcRootIndices(DumpContext ctx, BfsIndexCache bfsCache, Action<string>? update)
    {
        var rootIndices = new List<int>(4096);
        var seen        = new HashSet<ulong>(4096);

        // Try the GC-roots disk cache first (EnumerateRoots takes 250–300 s on large heaps)
        string gcRootCachePath = GcRootsCache.CachePath(ctx.DumpPath);
        if (GcRootsCache.IsValid(gcRootCachePath, ctx.DumpPath))
        {
            var cached = GcRootsCache.TryLoad(gcRootCachePath);
            if (cached is not null)
            {
                foreach (var addr in cached.Keys)
                    AddRoot(addr, bfsCache, rootIndices, seen);
                return rootIndices;
            }
        }

        // Derive roots from graph structure: objects with no incoming references.
        // Equivalent to GC roots for dominator purposes and runs in O(E) — no ClrMD scan.
        update?.Invoke("Deriving GC roots from graph (in-degree 0)…");
        int N = bfsCache.NodeCount;
        var hasIncoming = new bool[N]; // bool[87M] ≈ 87 MB
        for (int e = 0; e < bfsCache.EdgeCount; e++)
            hasIncoming[bfsCache.Children[e]] = true;
        for (int i = 0; i < N; i++)
            if (!hasIncoming[i])
                rootIndices.Add(i);
        return rootIndices;
    }

    private static void AddRoot(ulong addr, BfsIndexCache bfsCache, List<int> indices, HashSet<ulong> seen)
    {
        if (!seen.Add(addr)) return;
        if (bfsCache.TryGetIndex(addr, out int idx))
            indices.Add(idx);
    }
}
