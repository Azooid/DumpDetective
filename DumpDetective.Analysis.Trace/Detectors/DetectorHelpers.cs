using DumpDetective.Core.Models;
using DumpDetective.Core.Tracing;

namespace DumpDetective.Analysis.Trace.Detectors;

/// <summary>
/// Shared helpers for <see cref="ITracePatternDetector"/> implementations.
/// </summary>
internal static class DetectorHelpers
{
    /// <summary>
    /// Walks the subtree rooted at <paramref name="node"/> and returns up to
    /// <paramref name="maxFrames"/> unique method names that match any entry in
    /// <paramref name="patterns"/>. Depth is capped at <paramref name="maxDepth"/>.
    /// </summary>
    internal static List<string> CollectPatternEvidence(
        CallTreeNode node,
        string[] patterns,
        int maxDepth = 3,
        int maxFrames = 6)
    {
        var evidence = new List<string>(4);
        CollectRecursive(node, patterns, evidence, 0, maxDepth, maxFrames);
        return evidence;
    }

    private static void CollectRecursive(
        CallTreeNode node,
        string[] patterns,
        List<string> evidence,
        int depth,
        int maxDepth,
        int maxFrames)
    {
        if (depth > maxDepth) return;
        for (int i = 0; i < patterns.Length; i++)
        {
            if (node.Method.Contains(patterns[i], StringComparison.OrdinalIgnoreCase)
                && !evidence.Contains(node.Method))
            {
                evidence.Add(node.Method);
                break;
            }
        }
        for (int c = 0; c < node.Children.Count && evidence.Count < maxFrames; c++)
            CollectRecursive(node.Children[c], patterns, evidence, depth + 1, maxDepth, maxFrames);
    }
}
