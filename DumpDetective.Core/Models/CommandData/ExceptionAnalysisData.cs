namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>ExceptionAnalysisAnalyzer</c>.</summary>
/// <remarks>
/// Does not include <c>IsActive</c> / thread-ID enrichment — that requires
/// a thread enumeration pass done separately by the command.
/// </remarks>
public sealed record ExceptionAnalysisData(
    IReadOnlyDictionary<string, ExceptionTypeGroup> ByType,
    IReadOnlyDictionary<string, int>                Totals,
    int                                             TotalAll,
    FatalPrecursorFlags                             FatalFlags = default);

public sealed record ExceptionTypeGroup(
    string                          TypeName,
    IReadOnlyList<ExceptionHeapRecord> Samples);

public sealed record ExceptionHeapRecord(
    ulong                   Addr,
    string                  Type,
    string                  Message,
    int                     HResult,
    string?                 InnerType,
    IReadOnlyList<string>   StackFrames,
    /// <summary>Stable fingerprint of the stack trace (SHA-256 of joined non-blank frames). Empty when no frames.</summary>
    string                  StackHash = "");

/// <summary>Fatal / precursor exception pattern flags detected during heap walk.</summary>
public readonly record struct FatalPrecursorFlags(
    /// <summary>OutOfMemoryException instances present — OOM risk.</summary>
    bool HasOom,
    /// <summary>StackOverflowException or recursion signatures detected.</summary>
    bool HasStackOverflow,
    /// <summary>AccessViolationException or corrupted-state signals present.</summary>
    bool HasAccessViolation,
    /// <summary>ThreadAbortException present (legacy .NET / force-abort).</summary>
    bool HasThreadAbort,
    /// <summary>TypeInitializationException — static ctor failure, may cascade.</summary>
    bool HasTypeInitFailure,
    /// <summary>TargetInvocationException wrapping an unhandled exception.</summary>
    bool HasTargetInvocation);
