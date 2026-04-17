namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>ExceptionAnalysisAnalyzer</c>.</summary>
/// <remarks>
/// Does not include <c>IsActive</c> / thread-ID enrichment — that requires
/// a thread enumeration pass done separately by the command.
/// </remarks>
public sealed record ExceptionAnalysisData(
    IReadOnlyDictionary<string, ExceptionTypeGroup> ByType,
    IReadOnlyDictionary<string, int>                Totals,
    int                                             TotalAll);

public sealed record ExceptionTypeGroup(
    string                          TypeName,
    IReadOnlyList<ExceptionHeapRecord> Samples);

public sealed record ExceptionHeapRecord(
    ulong                   Addr,
    string                  Type,
    string                  Message,
    int                     HResult,
    string?                 InnerType,
    IReadOnlyList<string>   StackFrames);
