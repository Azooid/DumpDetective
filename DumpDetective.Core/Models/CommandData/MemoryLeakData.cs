namespace DumpDetective.Core.Models.CommandData;

public sealed record MemoryLeakData(
    IReadOnlyList<HeapStatRow>             AllTypes,
    IReadOnlyList<SuspectRow>              CountSuspects,   // non-system, high count (top 20)
    IReadOnlyList<SuspectRow>              SizeSuspects,    // high size, not in CountSuspects (top 10)
    long                                   TotalHeapSize,
    long                                   Gen0Total,
    long                                   Gen1Total,
    long                                   Gen2Total,
    long                                   LohTotal,
    long                                   PohTotal,
    int                                    TotalObjects,
    long                                   TotalStringSize,
    long                                   TotalStringCount,
    IReadOnlyList<MemoryRootChain>         RootChains,
    AccumulationPatternData                Patterns,
    int                                    MinCount = 500,
    int                                    TotalUniqueTypes = 0);

public sealed record SuspectRow(
    string Name, long Count, long Size, string Gen,
    long Gen2Count, long Gen2Size, long LohCount, long LohSize);

public sealed record AccumulationPatternData(
    long   StringCount,
    long   StringSize,
    long   StringGen2Size,
    long   ByteArrCount,
    long   ByteArrSize,
    long   ByteArrLohSize,
    long   ByteArrLohCount,
    long   CollTotalSize,
    long   DelegateCount,
    long   DelegateSize,
    long   TaskCount,
    long   TaskSize,
    IReadOnlyList<HeapStatRow>  TopCollections,    // up to 8 collection types
    IReadOnlyList<HeapStatRow>  TopDelegates,      // up to 6 delegate types
    IReadOnlyList<HeapStatRow>  TopTasks);         // up to 8 task/async types

public sealed record ChainStep(string Line, bool IsRoot);

public sealed record SampleChain(ulong Addr, long OwnSize, IReadOnlyList<ChainStep> Chain);

public sealed record MemoryRootChain(
    string                      TypeName,
    long                        Count,
    long                        TotalSize,
    IReadOnlyList<SampleChain>  SampleChains);
