using DumpDetective.Core.Models;

namespace DumpDetective.Core.Tracing;

/// <summary>
/// A contiguous "hot chain" — a subtree root that owns significant inclusive CPU,
/// linked down to the leaf with the highest exclusive CPU inside that subtree.
/// Gives engineers an immediately actionable root-cause chain without reading
/// the full call tree.
/// </summary>
public sealed record HotChain(
    /// <summary>The entry-point method that owns the subtree.</summary>
    string RootMethod,
    /// <summary>Full call chain from root down to the exclusive hot leaf, inclusive.</summary>
    IReadOnlyList<string> Chain,
    /// <summary>The method actually spending the most exclusive CPU in this subtree.</summary>
    string ExclusiveHotMethod,
    /// <summary>Inclusive CPU% of the root (how much of total trace this chain owns).</summary>
    double OwnerPct,
    /// <summary>Exclusive CPU% of the hot leaf (how much CPU the leaf itself burns).</summary>
    double HotPct);
