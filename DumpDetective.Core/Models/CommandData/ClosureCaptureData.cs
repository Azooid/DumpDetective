namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>ClosureCaptureAnalyzer</c>.</summary>
public sealed record ClosureCaptureData(
    IReadOnlyList<ClosureGroup> Groups,
    int                         TotalClosures,
    long                        TotalOwnSize,
    long                        TotalRetainedSize);

/// <summary>A group of closure display-class instances sharing the same declaring type.</summary>
public sealed record ClosureGroup(
    /// <summary>Fully qualified name of the enclosing class (before the '+').</summary>
    string DeclaringType,
    /// <summary>Compiler-generated closure type name (e.g. &lt;&gt;c__DisplayClass3_0).</summary>
    string ClosureTypeName,
    int    Count,
    long   OwnSizeTotal,
    long   RetainedSizeTotal,
    bool   IsEstimated,
    /// <summary>Names of captured fields discovered on a sample instance.</summary>
    IReadOnlyList<string> CapturedFields);
