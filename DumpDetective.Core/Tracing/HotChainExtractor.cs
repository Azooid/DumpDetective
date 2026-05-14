using DumpDetective.Core.Models;

namespace DumpDetective.Core.Tracing;

/// <summary>
/// Extracts <see cref="HotChain"/> records from a frozen <see cref="CallTreeNode"/> tree.
///
/// Algorithm for each top-level node whose InclusivePct ≥ minInclusivePct:
///  1. Record the root.
///  2. Descend by always choosing the child with the highest inclusive samples,
///     stopping when no child exceeds <paramref name="minInclusivePct"/> or the
///     current node's inclusive samples would increase (cycle guard).
///  3. Among all nodes visited, the one with the highest exclusive% is the hot leaf.
/// </summary>
public static class HotChainExtractor
{
    /// <param name="roots">Top-level call tree nodes (children of the virtual root).</param>
    /// <param name="minInclusivePct">
    ///     Minimum inclusive% a root must have to be included.
    ///     Defaults to 5 % — filters noise while keeping significant subtrees.
    /// </param>
    public static IReadOnlyList<HotChain> Extract(
        IReadOnlyList<CallTreeNode> roots,
        double minInclusivePct = 5.0)
    {
        var results = new List<HotChain>(roots.Count);

        foreach (var root in roots)
        {
            if (root.InclusivePct < minInclusivePct)
                continue;

            var chain = new List<string>(16) { root.Method };
            var cur   = root;

            // Track the node with the highest exclusive% in the chain
            var hotNode = root;

            while (cur.Children.Count > 0)
            {
                // Find the child with the highest inclusive count that is still
                // ≤ current (non-increasing → prevents cycle re-entry)
                CallTreeNode? best = null;
                for (int i = 0; i < cur.Children.Count; i++)
                {
                    var child = cur.Children[i];
                    if (child.InclusivePct < minInclusivePct / 2.0)
                        break; // children are sorted descending — nothing useful below
                    if (child.InclusiveSamples > cur.InclusiveSamples)
                        continue; // cycle guard
                    if (best is null || child.InclusiveSamples > best.InclusiveSamples)
                        best = child;
                }

                if (best is null) break;

                chain.Add(best.Method);

                if (best.ExclusivePct > hotNode.ExclusivePct)
                    hotNode = best;

                cur = best;
            }

            results.Add(new HotChain(
                RootMethod:          root.Method,
                Chain:               chain,
                ExclusiveHotMethod:  hotNode.Method,
                OwnerPct:            root.InclusivePct,
                HotPct:              hotNode.ExclusivePct));
        }

        // Sort: highest OwnerPct first
        results.Sort(static (a, b) => b.OwnerPct.CompareTo(a.OwnerPct));
        return results;
    }
}
