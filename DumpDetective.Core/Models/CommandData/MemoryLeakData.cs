namespace DumpDetective.Core.Models.CommandData;

public sealed record MemoryLeakData(
    IReadOnlyList<HeapStatRow>      AllTypes,
    IReadOnlyList<HeapStatRow>      AppSuspects,
    long                            TotalHeapSize,
    long                            TotalStringSize,
    long                            TotalStringCount,
    IReadOnlyList<MemoryRootChain>  RootChains);

public sealed record MemoryRootChain(
    string                  TypeName,
    ulong                   SampleAddr,
    IReadOnlyList<string>   Chain);
