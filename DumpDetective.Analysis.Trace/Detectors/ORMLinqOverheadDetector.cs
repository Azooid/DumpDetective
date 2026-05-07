using DumpDetective.Core.Models;
using DumpDetective.Core.Tracing;

namespace DumpDetective.Analysis.Trace.Detectors;

/// <summary>
/// Detects excessive in-memory LINQ traversal over materialised collections.
/// The pattern: data is fetched from the DB (or cache), fully materialised into
/// memory, and then filtered/aggregated with LINQ — meaning SQL WHERE clauses
/// were not used, and the full result set was loaded unnecessarily.
///
/// Signature: LINQ terminal operators (SingleOrDefault, FirstOrDefault, Where, ToList)
/// appearing high in the call tree alongside or directly under ORM/data-access frames.
/// </summary>
public sealed class ORMLinqOverheadDetector : ITracePatternDetector
{
    public string Name => "ORM LINQ Overhead";
    public double MinInclusivePct => 2.0;

    private static readonly string[] PrimaryPatterns =
    [
        "Enumerable.SingleOrDefault",
        "Enumerable.FirstOrDefault",
        "Enumerable.Where",
        "Enumerable.ToList",
        "Enumerable.OrderBy",
        "Enumerable.GroupBy",
        "Enumerable.SelectMany",
        "Enumerable.Count",
        "Enumerable.Any",
        "Enumerable.Sum",
        "Enumerable.Max",
        "Enumerable.Min",
    ];

    private static readonly string[] ContextPatterns =
    [
        "ObjectQuery",
        "EntityCollection",
        "DbSet",
        "IQueryable",
        "Repository",
        "DataContext",
        "NHibernate",
        "Dapper",
    ];

    public bool IsMatch(CallTreeNode node)
    {
        var method = node.Method;
        for (int i = 0; i < PrimaryPatterns.Length; i++)
            if (method.Contains(PrimaryPatterns[i], StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public TraceFinding Analyze(CallTreeNode node)
    {
        // Check whether ORM context frames appear nearby (children or method itself)
        var evidence = CollectEvidence(node);
        bool hasOrmContext = false;
        for (int i = 0; i < evidence.Count; i++)
        {
            for (int j = 0; j < ContextPatterns.Length; j++)
            {
                if (evidence[i].Contains(ContextPatterns[j], StringComparison.OrdinalIgnoreCase))
                {
                    hasOrmContext = true;
                    break;
                }
            }
            if (hasOrmContext) break;
        }

        // Identify the primary LINQ operator
        string operatorName = "LINQ";
        for (int i = 0; i < PrimaryPatterns.Length; i++)
        {
            if (node.Method.Contains(PrimaryPatterns[i], StringComparison.OrdinalIgnoreCase))
            {
                int dot = PrimaryPatterns[i].LastIndexOf('.');
                operatorName = dot >= 0 ? PrimaryPatterns[i][(dot + 1)..] : PrimaryPatterns[i];
                break;
            }
        }

        int score = (hasOrmContext, node.InclusivePct) switch
        {
            (true, >= 10) => 88,
            (true, >= 5)  => 80,
            (true, _)     => 70,
            (_, >= 10)    => 75,
            (_, >= 5)     => 65,
            _             => 55
        };

        string detail = hasOrmContext
            ? $"Enumerable.{operatorName} is executing over a materialised ORM collection. " +
              "This suggests data was loaded without a server-side filter — the full result set " +
              "was transferred from the database and filtered in memory."
            : $"Enumerable.{operatorName} is a hot call. " +
              "Large in-memory collections are being traversed repeatedly.";

        string advice = hasOrmContext
            ? "Push filtering to the database with IQueryable predicates (.Where() before .ToList()). " +
              "Use pagination (.Skip().Take()) for large result sets. " +
              "Check if AsQueryable() is accidentally losing query composability."
            : "Cache aggregation results if the source collection is not changing. " +
              "Replace repeated LINQ scans with pre-built dictionaries or sorted structures.";

        return new TraceFinding(
            Severity:       hasOrmContext ? FindingSeverity.Warning : FindingSeverity.Info,
            Category:       "ORM / LINQ",
            Headline:       hasOrmContext
                                ? $"Potential excessive in-memory LINQ ({operatorName}) after ORM materialisation"
                                : $"Heavy in-memory LINQ traversal ({operatorName})",
            Detail:         detail,
            Advice:         advice,
            Score:          score,
            Confidence:     hasOrmContext ? 0.75f : 0.45f,
            EvidenceFrames: [.. evidence]);
    }

    private static List<string> CollectEvidence(CallTreeNode node)
    {
        var evidence = new List<string>(6) { node.Method };
        for (int c = 0; c < node.Children.Count && evidence.Count < 8; c++)
            CollectEvidenceRecursive(node.Children[c], evidence, depth: 0);
        return evidence;
    }

    private static void CollectEvidenceRecursive(CallTreeNode node, List<string> evidence, int depth)
    {
        if (depth > 2) return;
        if (!evidence.Contains(node.Method))
            evidence.Add(node.Method);
        for (int c = 0; c < node.Children.Count && evidence.Count < 8; c++)
            CollectEvidenceRecursive(node.Children[c], evidence, depth + 1);
    }
}
