using DumpDetective.Core.Models;
using DumpDetective.Core.Tracing;

namespace DumpDetective.Analysis.Trace;

/// <summary>
/// Collapses noisy, well-known framework frames in a frozen <see cref="CallTreeNode"/> tree
/// into single synthetic nodes (e.g. "[ASP.NET Request Pipeline]").
///
/// This runs on the already-frozen tree returned by <c>CpuTraceAnalyzer</c>, producing
/// a new tree where repeated framework boilerplate is replaced by a single labelled node
/// whose samples are the sum of all collapsed frames within that run.
///
/// Algorithm per subtree:
/// 1. Walk children in inclusive-count order.
/// 2. When a child matches a <see cref="CollapseRule"/>, merge it and all its consecutive
///    siblings that match the SAME rule into one synthetic node that keeps their children.
/// 3. Non-matching children are recursed into as-is.
/// </summary>
public static class FrameworkCollapser
{
    /// <summary>
    /// Returns a new call tree with framework noise collapsed using the default rule set.
    /// The original tree is not mutated.
    /// </summary>
    public static IReadOnlyList<CallTreeNode> Collapse(IReadOnlyList<CallTreeNode> roots)
        => Collapse(roots, CollapseRuleSet.Default());

    /// <summary>
    /// Returns a new call tree with framework noise collapsed using a custom rule set.
    /// </summary>
    public static IReadOnlyList<CallTreeNode> Collapse(
        IReadOnlyList<CallTreeNode> roots,
        IReadOnlyList<CollapseRule> rules)
    {
        var result = new List<CallTreeNode>(roots.Count);
        foreach (var root in roots)
            result.Add(CollapseNode(root, rules));
        return result;
    }

    private static CallTreeNode CollapseNode(CallTreeNode node, IReadOnlyList<CollapseRule> rules)
    {
        if (node.Children.Count == 0)
            return node;

        var newChildren = CollapseChildren(node.Children, rules);
        return node with { Children = newChildren };
    }

    private static IReadOnlyList<CallTreeNode> CollapseChildren(
        IReadOnlyList<CallTreeNode> children,
        IReadOnlyList<CollapseRule> rules)
    {
        var result = new List<CallTreeNode>(children.Count);
        int i = 0;

        while (i < children.Count)
        {
            var child = children[i];
            var matchingRule = FindMatchingRule(child.Method, rules);

            if (matchingRule is null)
            {
                // Recurse normally — no collapse at this level
                result.Add(CollapseNode(child, rules));
                i++;
                continue;
            }

            // Merge this child and all consecutive siblings that match the SAME rule
            // into a single synthetic node. Combine their inclusive/exclusive samples
            // and flatten their children for further recursion.
            int mergedInclusive = child.InclusiveSamples;
            int mergedExclusive = child.ExclusiveSamples;
            double mergedInclusivePct = child.InclusivePct;
            double mergedExclusivePct = child.ExclusivePct;
            var mergedChildren = new List<CallTreeNode>(child.Children);

            i++;
            while (i < children.Count && MatchesRule(children[i].Method, matchingRule))
            {
                var sibling = children[i];
                mergedInclusive    += sibling.InclusiveSamples;
                mergedExclusive    += sibling.ExclusiveSamples;
                mergedInclusivePct += sibling.InclusivePct;
                mergedExclusivePct += sibling.ExclusivePct;
                mergedChildren.AddRange(sibling.Children);
                i++;
            }

            // Recurse into the merged children, then add the synthetic node
            var collapsedChildren = CollapseChildren(mergedChildren, rules);

            result.Add(new CallTreeNode(
                Method:           matchingRule.CollapsedLabel,
                Module:           matchingRule.Category,
                InclusiveSamples: mergedInclusive,
                ExclusiveSamples: mergedExclusive,
                InclusivePct:     mergedInclusivePct,
                ExclusivePct:     mergedExclusivePct,
                Children:         collapsedChildren));
        }

        return result;
    }

    private static CollapseRule? FindMatchingRule(string method, IReadOnlyList<CollapseRule> rules)
    {
        for (int r = 0; r < rules.Count; r++)
        {
            if (MatchesRule(method, rules[r]))
                return rules[r];
        }
        return null;
    }

    private static bool MatchesRule(string method, CollapseRule rule)
    {
        var patterns = rule.Patterns;
        for (int p = 0; p < patterns.Length; p++)
        {
            if (method.Contains(patterns[p], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
