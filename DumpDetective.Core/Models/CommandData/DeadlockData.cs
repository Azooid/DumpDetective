namespace DumpDetective.Core.Models.CommandData;

public sealed record DeadlockData(
    IReadOnlyList<BlockedThreadEntry> Blocked,
    IReadOnlyList<ContentionGroup>    Groups,
    int                               TotalThreadsByRuntime,
    int                               NamedThreadCount = 0);

public sealed record BlockedThreadEntry(
    int                   ManagedId,
    uint                  OSThreadId,
    string?               ThreadName,
    string                BlockType,
    string                BlockFrame,
    IReadOnlyList<string> StackFrames);

public sealed record ContentionGroup(
    string               LockType,
    IReadOnlyList<int>   ThreadIds,
    string               TopBlockFrame);
