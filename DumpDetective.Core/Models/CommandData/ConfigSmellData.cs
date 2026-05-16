namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Result of configuration smell analysis — detects likely misconfiguration of
/// ThreadPool, GC mode, and runtime settings from a memory dump snapshot.
/// </summary>
public sealed record ConfigSmellData(
    IReadOnlyList<ConfigSmell> Smells,
    /// <summary>Snapshot of observable config values extracted from the dump.</summary>
    ObservableRuntimeConfig    Config,
    int                        SmellCount);

/// <summary>A single detected configuration smell with severity and remediation advice.</summary>
public sealed record ConfigSmell(
    ConfigSmellSeverity Severity,
    string              Category,
    string              Title,
    string              Detail,
    string              Remediation,
    int                 Score);

public enum ConfigSmellSeverity { Info, Warning, Critical }

/// <summary>Observable runtime configuration values extracted from the dump.</summary>
public sealed record ObservableRuntimeConfig(
    bool   ServerGcEnabled,
    int    GcHeapCount,
    int    ProcessorCount,
    int    AliveThreadCount,
    int    ThreadPoolMinWorkers,
    int    ThreadPoolMaxWorkers,
    int    ThreadPoolMinIo,
    int    ThreadPoolMaxIo,
    int    BlockedThreadCount,
    string ClrVersion,
    bool   Is64Bit);
