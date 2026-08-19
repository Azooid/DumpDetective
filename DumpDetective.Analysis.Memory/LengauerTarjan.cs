namespace DumpDetective.Analysis.Memory;

/// <summary>
/// Classic Lengauer-Tarjan dominator algorithm with iterative DFS (no CLR-stack overflow)
/// and full path compression on EVAL.  O((V+E) α(V+E)) time.
///
/// All arrays are indexed by the caller's node indices, not by DFS position.
/// The caller is responsible for supplying augmented CSR arrays that include
/// the virtual root and its outgoing edges to GC-root nodes.
/// </summary>
public static class LengauerTarjan
{
    public readonly struct Result
    {
        /// <summary>Immediate dominator of each node (-1 = unreachable or virtual root itself).</summary>
        public readonly int[] Idom;
        /// <summary>Nodes in DFS preorder (vertex[0] = root).</summary>
        public readonly int[] Vertex;
        /// <summary>Number of reachable nodes (may be &lt; N if the graph is disconnected).</summary>
        public readonly int   ReachableCount;

        internal Result(int[] idom, int[] vertex, int reachableCount)
        {
            Idom           = idom;
            Vertex         = vertex;
            ReachableCount = reachableCount;
        }
    }

    /// <summary>
    /// Computes immediate dominators for all nodes reachable from <paramref name="root"/>.
    /// </summary>
    /// <param name="N">Total node count (node indices 0..N-1).</param>
    /// <param name="fwdOff">Forward CSR row offsets, length N+1.</param>
    /// <param name="fwdEdge">Forward CSR edge targets.</param>
    /// <param name="revOff">Reverse CSR row offsets, length N+1.</param>
    /// <param name="revEdge">Reverse CSR edge sources.</param>
    /// <param name="root">Start vertex (virtual root).</param>
    public static Result Compute(
        int               N,
        ReadOnlySpan<int> fwdOff,
        ReadOnlySpan<int> fwdEdge,
        ReadOnlySpan<int> revOff,
        ReadOnlySpan<int> revEdge,
        int               root)
    {
        // Working arrays (indexed by node index, not DFS number)
        var sdno     = GC.AllocateUninitializedArray<int>(N); // DFS preorder number
        var vertex   = GC.AllocateUninitializedArray<int>(N); // vertex[dfs#] → node idx
        var parent   = new int[N];                            // DFS tree parent (-1 = none)
        var semi     = GC.AllocateUninitializedArray<int>(N); // semidominator (as DFS #)
        var idom     = new int[N];                            // output: immediate dominator
        var ancestor = new int[N];                            // LINK/EVAL forest
        var label    = GC.AllocateUninitializedArray<int>(N); // EVAL min-semi label
        var bktHead  = new int[N];                            // bucket linked-list head
        var bktNext  = new int[N];                            // bucket linked-list next

        // Explicit DFS stack to avoid CLR stack overflow on deep object graphs
        var dfsStkNode = GC.AllocateUninitializedArray<int>(N);
        var dfsStkEdge = GC.AllocateUninitializedArray<int>(N);

        Array.Fill(sdno,     -1);
        Array.Fill(parent,   -1);
        Array.Fill(idom,     -1);
        Array.Fill(ancestor, -1);
        Array.Fill(bktHead,  -1);
        Array.Fill(bktNext,  -1);
        for (int i = 0; i < N; i++) { label[i] = i; semi[i] = i; }

        // ── Step 1: iterative DFS preorder numbering ──────────────────────────

        int dfsCnt = 0;
        int stkTop = 0;

        sdno[root]    = 0; vertex[0] = root; semi[root] = 0; dfsCnt = 1;
        dfsStkNode[0] = root;
        dfsStkEdge[0] = fwdOff[root];

        while (stkTop >= 0)
        {
            int v   = dfsStkNode[stkTop];
            int ei  = dfsStkEdge[stkTop];
            int end = fwdOff[v + 1];

            if (ei < end)
            {
                int w = fwdEdge[ei];
                dfsStkEdge[stkTop] = ei + 1;
                if (sdno[w] < 0) // first visit
                {
                    sdno[w] = dfsCnt; vertex[dfsCnt] = w; semi[w] = dfsCnt; dfsCnt++;
                    parent[w] = v;
                    stkTop++;
                    dfsStkNode[stkTop] = w;
                    dfsStkEdge[stkTop] = fwdOff[w];
                }
            }
            else
            {
                stkTop--;
            }
        }

        // ── Step 2: semidominators + implicit idom candidates ─────────────────

        var pathBuf = new List<int>(64); // reused scratch buffer for EVAL path compression

        for (int i = dfsCnt - 1; i >= 1; i--)
        {
            int w = vertex[i];

            // Update semidominator over all predecessors of w
            for (int e = revOff[w]; e < revOff[w + 1]; e++)
            {
                int v = revEdge[e];
                if (sdno[v] < 0) continue; // unreachable predecessor
                int u = Eval(v, ancestor, label, semi, pathBuf);
                if (semi[u] < semi[w]) semi[w] = semi[u];
            }

            // Add w to bucket of its semidominator node
            int sdomNode      = vertex[semi[w]];
            bktNext[w]        = bktHead[sdomNode];
            bktHead[sdomNode] = w;

            // Link w into the EVAL forest under its DFS tree parent
            ancestor[w] = parent[w];

            // Process the bucket that parent[w] now closes
            int pw = parent[w];
            int bv = bktHead[pw];
            bktHead[pw] = -1;
            while (bv >= 0)
            {
                int bnext = bktNext[bv];
                bktNext[bv] = -1;
                int y  = Eval(bv, ancestor, label, semi, pathBuf);
                idom[bv] = (semi[y] < semi[bv]) ? y : pw;
                bv = bnext;
            }
        }

        // ── Step 3: finalize idom (convert semidom-relative to actual idom) ───

        idom[root] = root; // root dominates itself (callers will interpret as "no parent")
        for (int i = 1; i < dfsCnt; i++)
        {
            int w = vertex[i];
            if (idom[w] != vertex[semi[w]])
                idom[w] = idom[idom[w]];
        }

        return new Result(idom, vertex, dfsCnt);
    }

    // Iterative EVAL with full path compression.
    // Collects the path from v to the forest root, then updates labels back-to-front.
    private static int Eval(int v, int[] ancestor, int[] label, int[] semi, List<int> pathBuf)
    {
        if (ancestor[v] < 0) return label[v];

        pathBuf.Clear();
        int cur = v;
        while (ancestor[cur] >= 0)
        {
            pathBuf.Add(cur);
            cur = ancestor[cur];
        }
        // cur is now the forest root (ancestor[cur] == -1)

        // Walk back towards v, updating labels and compressing ancestors to root
        for (int i = pathBuf.Count - 1; i >= 0; i--)
        {
            int node = pathBuf[i];
            int anc  = ancestor[node];
            if (semi[label[anc]] < semi[label[node]])
                label[node] = label[anc];
            ancestor[node] = cur; // path compress: skip to forest root
        }

        return label[v];
    }
}
