using DumpDetective.Core.Tracing;

namespace DumpDetective.Analysis.Trace;

/// <summary>
/// Groups a list of <see cref="TraceFinding"/> records by Category and computes
/// a per-category aggregate score.
///
/// Scoring: the category score is the maximum Score among its findings.
/// This ensures that even a single high-severity finding surfaces prominently.
/// </summary>
public static class CategoryScorer
{
    /// <summary>
    /// Returns per-category scores sorted by score descending.
    /// </summary>
    public static IReadOnlyList<CategoryScore> Score(IReadOnlyList<TraceFinding> findings)
    {
        if (findings.Count == 0)
            return [];

        // Group by Category — manual loop to avoid LINQ in what may be a hot path
        // when called from the orchestrator across many sub-reports
        var groups = new Dictionary<string, (int MaxScore, int Count, TraceFinding Best)>(
            StringComparer.Ordinal);

        for (int i = 0; i < findings.Count; i++)
        {
            var f = findings[i];
            if (groups.TryGetValue(f.Category, out var g))
            {
                if (f.Score > g.MaxScore)
                    groups[f.Category] = (f.Score, g.Count + 1, f);
                else
                    groups[f.Category] = (g.MaxScore, g.Count + 1, g.Best);
            }
            else
            {
                groups[f.Category] = (f.Score, 1, f);
            }
        }

        var result = new List<CategoryScore>(groups.Count);
        foreach (var kv in groups)
            result.Add(new CategoryScore(kv.Key, kv.Value.MaxScore, kv.Value.Count, kv.Value.Best));

        result.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        return result;
    }
}
