namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// JIT compilation events from a .nettrace / .etl trace.
/// Tracks which methods were JIT-compiled, total JIT time, and hottest methods by compile time.
/// </summary>
public sealed record JitTraceData(
    string TraceInfo,
    string? FilteredProcess,
    int TotalMethodsJitted,
    double TotalJitTimeMs,
    double MaxJitTimeMs,
    double AvgJitTimeMs,
    /// <summary>Methods that took the longest to JIT-compile.</summary>
    IReadOnlyList<JitMethodEntry> TopByTime,
    /// <summary>Methods JIT-compiled most frequently (dynamic methods, re-compilations).</summary>
    IReadOnlyList<JitMethodEntry> TopByCount,
    /// <summary>Modules with the most JIT activity.</summary>
    IReadOnlyList<JitModuleSummary> TopModules,
    /// <summary>True if JIT events were found but had no timing (e.g. EventPipe without verb).</summary>
    bool TimingAvailable);

public sealed record JitMethodEntry(
    string MethodName,
    string ModuleName,
    int CompileCount,
    double TotalJitTimeMs,
    double MaxJitTimeMs,
    int ILSize,
    int NativeSize);

public sealed record JitModuleSummary(
    string ModuleName,
    int MethodCount,
    double TotalJitTimeMs);
