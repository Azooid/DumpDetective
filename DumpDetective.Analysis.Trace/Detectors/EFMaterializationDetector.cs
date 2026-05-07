using DumpDetective.Core.Models;
using DumpDetective.Core.Tracing;

namespace DumpDetective.Analysis.Trace.Detectors;

/// <summary>
/// Detects heavy Entity Framework entity materialisation in the call tree.
/// Recognises the EF6 shaper pipeline, ObjectQuery execution, and EntityCollection loading.
/// </summary>
public sealed class EFMaterializationDetector : ITracePatternDetector
{
    public string Name => "EF Materialisation";
    public double MinInclusivePct => 2.0;

    private static readonly string[] Patterns =
    [
        "Shaper.HandleEntityAppendOnly",
        "Shaper.HandleIEntityWithKey",
        "SimpleEnumerator.MoveNext",        // EF6: Shaper`1+SimpleEnumerator.MoveNext
        "Coordinator.ReadNextElement",       // EF6: Coordinator`1.ReadNextElement
        "ObjectMaterializer",
        "ObjectQuery.Execute",
        "EntityCollection.Load",
        "EntityCollection.GetResults",
        "ObjectContext.ExecuteStoreQuery",
        "EntityFramework.Core.Query",
        "InternalDbQuery",
        "DbDataReader.GetValues",
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
        // Identify which specific patterns are present in this subtree
        var evidence = CollectEvidence(node);
        bool hasRelationLoad  = evidence.Any(e => e.Contains("EntityCollection", StringComparison.OrdinalIgnoreCase));
        bool hasGraphHydration = evidence.Count >= 3;

        int score = node.InclusivePct switch
        {
            >= 20 => 95,
            >= 10 => 88,
            >= 5  => 80,
            _     => 70
        };

        string headline = hasGraphHydration
            ? "Heavy EF entity graph hydration — multiple materialisation stages detected"
            : "EF entity materialisation is a significant CPU consumer";

        string detail = hasRelationLoad
            ? "EntityCollection.Load calls indicate lazy-loaded relation traversal. " +
              "Each navigation property access may issue a separate SQL query (N+1 pattern)."
            : "Entity materialisation is consuming CPU. Large result sets are being shaped into managed objects.";

        string advice = hasGraphHydration
            ? "Use .Include() for eager loading to avoid N+1 queries. " +
              "Project to DTOs with .Select() to reduce the graph size. " +
              "Consider caching hydrated objects for read-heavy scenarios."
            : "Use .AsNoTracking() for read-only queries. " +
              "Project to DTOs with .Select() instead of materialising full entities.";

        return new TraceFinding(
            Severity:       FindingSeverity.Warning,
            Category:       "Entity Framework",
            Headline:       headline,
            Detail:         detail,
            Advice:         advice,
            Score:          score,
            Confidence:     Math.Min(1.0f, (float)(evidence.Count / 3.0)),
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
