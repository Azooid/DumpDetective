using DumpDetective.Core.Models;
using DumpDetective.Core.Tracing;

namespace DumpDetective.Analysis.Trace.Detectors;

/// <summary>
/// Detects significant CPU consumption caused by DataTable/DataRow operations.
/// DataTable is notorious for CPU overhead due to boxing, events, and constraint validation
/// on every cell write. High rates indicate the data model should be migrated to
/// typed collections or POCOs.
/// </summary>
public sealed class DataTableDetector : ITracePatternDetector
{
    public string Name => "DataTable Overhead";
    public double MinInclusivePct => 1.5;

    private static readonly string[] Patterns =
    [
        "DataRow.set_Item",
        "DataRow.get_Item",
        "DataTable.Copy",
        "DataTable.Clone",
        "DataTable.Merge",
        "DataTable.LoadDataRow",
        "DataColumnCollection",
        "DataColumn.set_",
        "DataRowCollection.Add",
        "DataView.FindRows",
        "DataView.Sort",
        "ConstraintCollection",
        "UniqueConstraint",
        "ForeignKeyConstraint",
    ];

    public bool IsMatch(CallTreeNode node)
    {
        var method = node.Method;
        for (int i = 0; i < Patterns.Length; i++)
            if (method.Contains(Patterns[i], StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public TraceFinding Analyze(CallTreeNode node)
    {
        var evidence = CollectEvidence(node);
        bool hasConstraints = evidence.Any(e =>
            e.Contains("Constraint", StringComparison.OrdinalIgnoreCase));
        bool hasSort = evidence.Any(e =>
            e.Contains("Sort", StringComparison.OrdinalIgnoreCase) ||
            e.Contains("FindRows", StringComparison.OrdinalIgnoreCase));

        int score = node.InclusivePct switch
        {
            >= 10 => 85,
            >= 5  => 75,
            >= 2  => 65,
            _     => 55
        };

        string detail = (hasConstraints, hasSort) switch
        {
            (true, _)  => "DataTable constraint validation is consuming CPU. " +
                          "Every DataRow.set_Item call triggers constraint checks, " +
                          "raising events and potentially scanning sibling rows.",
            (_, true)  => "DataView sort/find operations over DataTable are a bottleneck. " +
                          "DataView sort uses string-based column comparison which is slow at scale.",
            _          => "DataTable/DataRow cell access is a significant CPU consumer. " +
                          "DataRow indexers involve boxing, null checks, and event notifications per access."
        };

        return new TraceFinding(
            Severity:       FindingSeverity.Warning,
            Category:       "DataTable Overhead",
            Headline:       "High DataTable/DataRow overhead detected",
            Detail:         detail,
            Advice:         "Replace DataTable with typed List<T> or record collections. " +
                            "Use BeginLoadData()/EndLoadData() to suspend constraints during bulk inserts. " +
                            "For reporting/read scenarios, consider converting to POCO lists once and caching.",
            Score:          score,
            Confidence:     Math.Min(1.0f, (float)(evidence.Count / 2.0)),
            EvidenceFrames: [.. evidence]);
    }

    private static List<string> CollectEvidence(CallTreeNode node)
    {
        var evidence = new List<string>(4);
        CollectEvidenceRecursive(node, evidence, depth: 0);
        return evidence;
    }

    private static void CollectEvidenceRecursive(CallTreeNode node, List<string> evidence, int depth)
    {
        if (depth > 3) return;
        for (int i = 0; i < Patterns.Length; i++)
        {
            if (node.Method.Contains(Patterns[i], StringComparison.OrdinalIgnoreCase)
                && !evidence.Contains(node.Method))
            {
                evidence.Add(node.Method);
                break;
            }
        }
        for (int c = 0; c < node.Children.Count && evidence.Count < 6; c++)
            CollectEvidenceRecursive(node.Children[c], evidence, depth + 1);
    }
}
