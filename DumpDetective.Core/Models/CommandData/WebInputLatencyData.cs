using DumpDetective.Core.Models;

namespace DumpDetective.Core.Models.CommandData;

public sealed class WebInputLatencyRow
{
    /// <summary>The interaction kind — e.g. "MouseDown", "GestureScrollUpdate", "Click".</summary>
    public required string Kind        { get; init; }
    public required long   TimestampUs { get; init; }
    public required long   DurationUs  { get; init; }

    /// <summary>The long task (if any) whose window overlapped this interaction's start — what the main thread was actually busy doing while the user waited. Null when no long task overlapped it.</summary>
    public string? BlockedByFunction     { get; init; }
    public string? BlockedByUrl          { get; init; }
    public int     BlockedByLine         { get; init; } = -1;
    public string? BlockedByResolvedFile { get; init; }
    public int     BlockedByResolvedLine { get; init; } = -1;

    /// <summary>"file.ts:42" when resolved, else the minified bundle "url:line", else "—" when no blocker was found.</summary>
    public string BlockedByLocation => BlockedByFunction is null
        ? "—"
        : BlockedByResolvedFile is not null
            ? $"{BlockedByResolvedFile}:{BlockedByResolvedLine}"
            : BlockedByUrl is { Length: > 0 } ? $"{BlockedByUrl}:{BlockedByLine}" : "(native)";
}

public sealed class WebInputLatencyData
{
    public required string TraceInfo { get; init; }
    public int    SampleCount { get; init; }
    public long   MedianUs    { get; init; }
    public long   P95Us       { get; init; }
    public long   MaxUs       { get; init; }
    public required IReadOnlyList<WebInputLatencyRow> WorstEvents { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
}
