using DumpDetective.Core.Models;
using DumpDetective.Core.Tracing;
using DumpDetective.Analysis.Trace.Detectors;

namespace DumpDetective.Analysis.Trace;

/// <summary>
/// Runs a set of <see cref="ITracePatternDetector"/> instances against a frozen
/// <see cref="CallTreeNode"/> tree and returns the deduplicated, ranked list of
/// <see cref="TraceFinding"/> records.
///
/// Walk strategy:
/// - Every node in the tree is visited exactly once (DFS pre-order).
/// - For each node, each detector is asked whether it matches (cheap pre-filter).
/// - If matched AND the node's InclusivePct ≥ the detector's MinInclusivePct,
///   the detector's Analyze method is called.
/// - At most one finding per (Category × RootMethod) pair is kept — the highest-scored one.
///   This prevents duplicate findings when the same pattern appears at multiple tree depths.
/// </summary>
public static class SemanticAnalyzer
{
    /// <summary>
    /// Returns the default detector pack covering EF, dynamic property access,
    /// DataTable overhead, and ORM/LINQ inefficiency.
    /// </summary>
    public static IReadOnlyList<ITracePatternDetector> DefaultDetectors { get; } =
    [
        new EFMaterializationDetector(),
        new DynamicPropertyDetector(),
        new DataTableDetector(),
        new ORMLinqOverheadDetector(),
    ];

    /// <summary>
    /// Runs the default detector set against the provided call tree roots.
    /// </summary>
    public static IReadOnlyList<TraceFinding> Analyze(IReadOnlyList<CallTreeNode> roots)
        => Analyze(roots, DefaultDetectors);

    /// <summary>
    /// Runs the provided detector set against the call tree, returning ranked findings.
    /// </summary>
    public static IReadOnlyList<TraceFinding> Analyze(
        IReadOnlyList<CallTreeNode> roots,
        IReadOnlyList<ITracePatternDetector> detectors)
    {
        // Key = Category, to deduplicate per-category to the highest-scored finding
        var bestPerCategory = new Dictionary<string, TraceFinding>(StringComparer.Ordinal);

        for (int r = 0; r < roots.Count; r++)
            WalkNode(roots[r], detectors, bestPerCategory);

        var results = new List<TraceFinding>(bestPerCategory.Count);
        foreach (var kv in bestPerCategory)
            results.Add(kv.Value);

        // Sort: Critical first, then by score descending
        results.Sort(static (a, b) =>
        {
            int sev = b.Severity.CompareTo(a.Severity);
            return sev != 0 ? sev : b.Score.CompareTo(a.Score);
        });

        return results;
    }

    private static void WalkNode(
        CallTreeNode node,
        IReadOnlyList<ITracePatternDetector> detectors,
        Dictionary<string, TraceFinding> bestPerCategory)
    {
        // Run detectors on this node
        for (int d = 0; d < detectors.Count; d++)
        {
            var detector = detectors[d];

            if (node.InclusivePct < detector.MinInclusivePct)
                continue;

            if (!detector.IsMatch(node))
                continue;

            var finding = detector.Analyze(node);

            // Keep only the highest-scored finding per Category
            if (!bestPerCategory.TryGetValue(finding.Category, out var existing)
                || finding.Score > existing.Score)
            {
                bestPerCategory[finding.Category] = finding;
            }
        }

        // Recurse into children
        for (int c = 0; c < node.Children.Count; c++)
            WalkNode(node.Children[c], detectors, bestPerCategory);
    }
}
