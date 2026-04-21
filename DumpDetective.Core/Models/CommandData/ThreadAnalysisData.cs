namespace DumpDetective.Core.Models.CommandData;

public sealed record ThreadAnalysisData(
    IReadOnlyList<ThreadInfo> Threads,
    int                       TotalCount,
    int                       AliveCount,
    int                       BlockedCount,
    int                       WithExceptionCount,
    int                       NamedCount);

public sealed record ThreadInfo(
    int                     ManagedId,
    uint                    OSThreadId,
    string?                 Name,
    bool                    IsAlive,
    string                  Category,
    string                  GcMode,
    string?                 Exception,
    string?                 LockInfo,
    IReadOnlyList<string>   StackFrames);
