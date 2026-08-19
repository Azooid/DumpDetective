using DumpDetective.Core.Models;

namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data produced by <c>DomTreeAnalyzer</c>.</summary>
public sealed record DominatorTreeData(
    IReadOnlyList<DomRetainerNode> Roots,
    long                           TotalHeapBytes,
    long                           TotalReachableObjects,
    int                            DisplayDepth,
    bool                           IsTruncated);
