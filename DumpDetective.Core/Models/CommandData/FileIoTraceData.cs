namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Results from file I/O trace analysis.
/// Populated from Microsoft-Windows-Kernel-File provider events (ETL only).
/// Returns HasData=false for .nettrace input (kernel events not available in EventPipe).
/// </summary>
public sealed record FileIoTraceData(
    string TraceInfo,
    string? FilteredProcess,
    int    TotalReads,
    int    TotalWrites,
    int    TotalOther,
    long   TotalReadBytes,
    long   TotalWriteBytes,
    int    SlowOpCount,
    IReadOnlyList<FileIoSlowOp>      SlowOps,
    IReadOnlyList<FileIoFileSummary>  TopFiles,
    IReadOnlyList<double>?            BytesTimeline,
    bool HasData);

/// <summary>A single slow file I/O operation.</summary>
public sealed record FileIoSlowOp(
    string FilePath,
    string OperationType,
    double DurationMs,
    long   Bytes,
    double TimeMs);

/// <summary>Aggregate I/O statistics per file path.</summary>
public sealed record FileIoFileSummary(
    string FilePath,
    int    ReadCount,
    int    WriteCount,
    long   TotalBytes,
    double TotalMs);
