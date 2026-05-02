namespace DumpDetective.Core.Models;

/// <summary>
/// Node in an inclusive call tree produced by CPU or allocation trace analysis.
/// Children are sorted descending by <see cref="InclusiveSamples"/>.
/// </summary>
public sealed record CallTreeNode(
    string                        Method,
    string                        Module,
    int                           InclusiveSamples,
    int                           ExclusiveSamples,
    double                        InclusivePct,
    double                        ExclusivePct,
    IReadOnlyList<CallTreeNode>   Children);
