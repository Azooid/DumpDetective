namespace DumpDetective.Core.Models.CommandData;

/// <summary>The inferred workload type for a process dump.</summary>
public enum WorkloadKind
{
    Unknown,
    AspNetCore,
    GrpcService,
    BackgroundWorker,
    DataService,
    Console,
    WinForms,
    Wpf,
    Orleans,
}

/// <summary>Result of workload profile classification.</summary>
public sealed record WorkloadProfileData(
    WorkloadKind              Kind,
    string                    Label,
    string                    Rationale,
    int                       Confidence,
    IReadOnlyList<string>     DetectedSignals,
    /// <summary>Recommended analyzer set for this workload kind.</summary>
    IReadOnlyList<string>     RecommendedCommands);
