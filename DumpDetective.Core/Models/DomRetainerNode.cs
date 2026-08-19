namespace DumpDetective.Core.Models;

/// <summary>
/// A type-collapsed node in the dominator tree, produced by <c>DomTreeAnalyzer</c>
/// and consumed by <see cref="Interfaces.IRenderSink.DomTree"/>.
/// </summary>
public sealed record DomRetainerNode(
    string                        TypeName,
    int                           InstanceCount,
    long                          ShallowBytes,
    long                          RetainedBytes,
    double                        RetainedPct,
    IReadOnlyList<DomRetainerNode> Children);
