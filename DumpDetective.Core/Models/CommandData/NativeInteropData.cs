namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>NativeInteropAnalyzer</c>.</summary>
public sealed record NativeInteropData(
    IReadOnlyList<NativeThreadEntry>  Threads,
    int                               TotalThreadsWithNativeFrames,
    IReadOnlyList<NativeFrameSummary> TopNativeCallSites);

/// <summary>A thread that has one or more native/runtime transition frames.</summary>
public sealed record NativeThreadEntry(
    int                             ManagedId,
    uint                            OSThreadId,
    string                          Category,
    IReadOnlyList<NativeStackFrame> Frames,
    int                             NativeFrameCount,
    string                          DeepestNativeFrame);

/// <summary>One frame in a mixed managed+native thread stack.</summary>
public sealed record NativeStackFrame(
    string FrameName,
    bool   IsNative);

/// <summary>Aggregated native call site seen across multiple threads.</summary>
public sealed record NativeFrameSummary(
    string FrameName,
    int    ThreadCount);
