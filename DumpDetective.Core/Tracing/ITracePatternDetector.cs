using DumpDetective.Core.Models;

namespace DumpDetective.Core.Tracing;

/// <summary>
/// A pluggable semantic detector that inspects a <see cref="CallTreeNode"/> subtree
/// and emits a <see cref="TraceFinding"/> when it recognises a known performance pattern.
///
/// Implementation contract:
/// <list type="bullet">
///   <item><description>
///     <see cref="IsMatch"/> MUST be a cheap pre-filter (string comparisons only, no allocations).
///     It is called on every node in the tree; only nodes that pass are forwarded to <see cref="Analyze"/>.
///   </description></item>
///   <item><description>
///     <see cref="Analyze"/> is called after <see cref="IsMatch"/> returns true AND
///     the node's <see cref="CallTreeNode.InclusivePct"/> ≥ <see cref="MinInclusivePct"/>.
///     It may do more expensive scoring but must still avoid allocating large collections.
///   </description></item>
///   <item><description>
///     A detector is stateless — the same instance is reused across all nodes.
///   </description></item>
/// </list>
/// </summary>
public interface ITracePatternDetector
{
    /// <summary>Human-readable detector name, used in logs and test assertions.</summary>
    string Name { get; }

    /// <summary>
    /// Minimum inclusive CPU% a node must have before <see cref="Analyze"/> is called.
    /// Cheap pre-filter: set to 1.0 for broad detectors, higher for targeted ones.
    /// </summary>
    double MinInclusivePct { get; }

    /// <summary>
    /// Fast pre-filter. Return true if the node's method name contains any of the
    /// detector's signature strings.  No allocations allowed here.
    /// </summary>
    bool IsMatch(CallTreeNode node);

    /// <summary>
    /// Produces a <see cref="TraceFinding"/> for the matched node.
    /// Called only when <see cref="IsMatch"/> returned true and the inclusive% threshold
    /// was met. May walk <see cref="CallTreeNode.Children"/> up to two levels deep.
    /// </summary>
    TraceFinding Analyze(CallTreeNode node);
}
