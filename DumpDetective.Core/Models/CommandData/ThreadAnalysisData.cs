namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Three-way classification of a thread's synchronisation state.
/// </summary>
public enum WaitKind
{
    /// <summary>Thread is running or idle — not waiting on a sync primitive.</summary>
    None,
    /// <summary>Thread is trying to enter a Monitor (lock statement). Indicates lock contention.</summary>
    Monitor,
    /// <summary>Thread is independently waiting on WaitHandle/Task/SemaphoreSlim/etc.
    /// This is normal for background workers, timers, and APM dispatchers.</summary>
    Independent,
}

public sealed record ThreadAnalysisData(
    IReadOnlyList<ThreadInfo> Threads,
    int                       TotalCount,
    int                       AliveCount,
    /// <summary>Threads waiting to enter a Monitor lock (contended lock — may indicate deadlock).</summary>
    int                       MonitorBlockedCount,
    /// <summary>Threads waiting on WaitHandle/Task/Semaphore — normal background waiting.</summary>
    int                       IndependentWaitCount,
    int                       WithExceptionCount,
    int                       NamedCount)
{
    /// <summary>Total of Monitor + Independent — kept for backward compat with existing report code.</summary>
    public int BlockedCount => MonitorBlockedCount + IndependentWaitCount;
};

public sealed record ThreadInfo(
    int                     ManagedId,
    uint                    OSThreadId,
    string?                 Name,
    bool                    IsAlive,
    string                  Category,
    string                  GcMode,
    string?                 Exception,
    string?                 LockInfo,
    WaitKind                WaitKind,
    IReadOnlyList<string>   StackFrames);
