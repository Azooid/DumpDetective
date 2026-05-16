namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Result of sync-block + wait-chain analysis.
/// </summary>
public sealed record DeadlockData(
    /// <summary>Monitor (sync-block) locks — each has a known owner and 0..N waiters.</summary>
    IReadOnlyList<MonitorLockEntry>   MonitorLocks,
    /// <summary>Confirmed cyclic wait chains (T1→T2→T1). Non-empty = deadlock.</summary>
    IReadOnlyList<DeadlockCycle>      ConfirmedCycles,
    /// <summary>Threads blocked on WaitOne/WaitAny/Task.Wait etc. — independent, no ownership chain.</summary>
    IReadOnlyList<IndependentWaiter>  IndependentWaiters,
    int                               TotalThreadsByRuntime,
    int                               NamedThreadCount = 0,
    /// <summary>
    /// Directed wait-for graph edges suitable for visualization.
    /// Each edge: WaiterThreadId → OwnerThreadId, annotated with lock info.
    /// </summary>
    IReadOnlyList<WaitForEdge>        WaitForGraph = default!);

/// <summary>One inflated monitor lock from the sync-block table.</summary>
public sealed record MonitorLockEntry(
    ulong              LockAddress,
    string             LockTypeName,
    int?               OwnerManagedId,
    uint?              OwnerOSId,
    string?            OwnerThreadName,
    /// <summary>Threads waiting to enter this monitor.</summary>
    IReadOnlyList<int> WaiterManagedIds,
    int                RecursionCount);

/// <summary>A confirmed deadlock: every thread in the cycle owns one lock while waiting for the next.</summary>
public sealed record DeadlockCycle(
    /// <summary>Thread IDs in cycle order, e.g. [T12, T18, T12].</summary>
    IReadOnlyList<int>              ThreadIds,
    /// <summary>Lock addresses held at each step in the cycle. Parallel to ThreadIds.</summary>
    IReadOnlyList<ulong>            LockAddresses = default!,
    /// <summary>Lock type names at each step. Parallel to ThreadIds.</summary>
    IReadOnlyList<string>           LockTypeNames = default!);

/// <summary>A thread blocked on a non-Monitor wait (WaitOne, WaitAny, Task.Wait, Thread.Join…).</summary>
public sealed record IndependentWaiter(
    int                   ManagedId,
    uint                  OSThreadId,
    string?               ThreadName,
    /// <summary>Human-readable block reason, e.g. "WaitHandle.WaitOne".</summary>
    string                BlockReason,
    /// <summary>Top user-code frame for context.</summary>
    string                TopUserFrame,
    IReadOnlyList<string> StackFrames);

/// <summary>
/// One directed edge in the wait-for graph: waiter is blocked on a lock owned by owner.
/// Suitable for adjacency-list graph rendering.
/// </summary>
public sealed record WaitForEdge(
    int    WaiterManagedId,
    int    OwnerManagedId,
    ulong  LockAddress,
    string LockTypeName,
    /// <summary>True when this edge participates in a confirmed cycle.</summary>
    bool   IsInCycle);
