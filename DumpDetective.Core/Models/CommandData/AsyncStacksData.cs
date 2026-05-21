namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>AsyncStacksAnalyzer</c>.</summary>
public sealed record AsyncStacksData(
    IReadOnlyList<StateMachineEntry>  Entries,
    int                               BacklogTotal,
    /// <summary>
    /// Inferred async call chains grouped by shared namespace/class prefix.
    /// Each chain shows the likely call stack of suspended async methods
    /// (e.g. RequestHandler → OrderService → SqlRepository).
    /// </summary>
    IReadOnlyList<AsyncDepChain>      DepChains = default!,
    /// <summary>
    /// BFS-retained memory per suspended async method group (populated when a
    /// BFS index cache is available; null otherwise).
    /// </summary>
    IReadOnlyList<StateMachineRetained>? RetainedByMethod = null);

/// <summary>One heap-resident async state-machine instance.</summary>
public readonly record struct StateMachineEntry(string Method, string State, ulong Addr);

/// <summary>BFS-retained memory for a group of suspended async state machines.</summary>
public sealed record StateMachineRetained(
    string Method,
    int    Count,
    long   OwnSizeTotal,
    long   RetainedSizeTotal,
    bool   IsEstimated);

/// <summary>
/// A reconstructed async dependency chain inferred from name-prefix clustering
/// of suspended state machines.
/// </summary>
public sealed record AsyncDepChain(
    /// <summary>Root / entry-point method name (shortest common ancestor).</summary>
    string Root,
    /// <summary>Ordered list of method names from Root to the deepest await point.</summary>
    IReadOnlyList<string> Chain,
    /// <summary>Number of state machine instances contributing to this chain.</summary>
    int InstanceCount,
    /// <summary>True when the deepest node is awaiting an I/O, SQL, or network primitive.</summary>
    bool LikelyBlockedOnIo);
