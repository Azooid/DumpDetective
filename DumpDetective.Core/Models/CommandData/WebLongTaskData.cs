using DumpDetective.Core.Models;

namespace DumpDetective.Core.Models.CommandData;

public sealed class WebLongTaskRow
{
    public long TimestampUs { get; init; }
    public long DurationUs  { get; init; }
    public int  Pid         { get; init; }
    public int  Tid         { get; init; }

    /// <summary>Best-guess function running during this task, correlated from bucketed CPU self-time — null when no CPU profile data overlaps this task's window.</summary>
    public string? AttributedFunction     { get; init; }
    public string? AttributedUrl          { get; init; }
    public int     AttributedLine         { get; init; } = -1;
    public string? AttributedResolvedFile { get; init; }
    public int     AttributedResolvedLine { get; init; } = -1;
    public long    AttributedSelfTimeUs   { get; init; }
    /// <summary>Nearest callers of the attributed function, nearest first — often the real answer to "which feature caused this", since a shared utility's own name rarely says.</summary>
    public string? AttributedCallChain    { get; init; }

    // ── Possible trigger — a timing correlation, not a call-tree link ──────────────────
    // See ChromeTraceParser.AttributePossibleTriggers: the nearest first-party sample
    // seen before this task started, for the case leaf/call-chain attribution can't cover
    // (a function that schedules work via setTimeout/a promise and returns immediately).
    public string? PossibleTriggerFunction     { get; init; }
    public string? PossibleTriggerUrl          { get; init; }
    public int     PossibleTriggerLine         { get; init; } = -1;
    public string? PossibleTriggerResolvedFile { get; init; }
    public int     PossibleTriggerResolvedLine { get; init; } = -1;
    public long    PossibleTriggerGapUs        { get; init; }
    /// <summary>Every distinct first-party function seen in the second before this task started, not just the nearest one — a burst of setup/render code shows up here as a group. Formatted, most recent first.</summary>
    public string? PossibleTriggerCluster      { get; init; }

    /// <summary>"file.ts:42" when resolved, else the minified bundle "url:line", else "—" when unattributed.</summary>
    public string AttributedLocation => AttributedFunction is null
        ? "—"
        : AttributedResolvedFile is not null
            ? $"{AttributedResolvedFile}:{AttributedResolvedLine}"
            : AttributedUrl is { Length: > 0 } ? $"{AttributedUrl}:{AttributedLine}" : "(native)";

    /// <summary>"file.ts:42" when resolved, else the minified bundle "url:line", else "—" when no trigger found.</summary>
    public string PossibleTriggerLocation => PossibleTriggerFunction is null
        ? "—"
        : PossibleTriggerResolvedFile is not null
            ? $"{PossibleTriggerResolvedFile}:{PossibleTriggerResolvedLine}"
            : PossibleTriggerUrl is { Length: > 0 } ? $"{PossibleTriggerUrl}:{PossibleTriggerLine}" : "(native)";
}

/// <summary>
/// One distinct attributed cause, with every long task blamed on it rolled up — the
/// "fix this one thing and N tasks / X ms of blocking time go away" view. Ranked by
/// total blocked time, not task count, since one function causing three 2-second freezes
/// matters more than another causing ten 60ms ones.
/// </summary>
public sealed class WebLongTaskRootCause
{
    public required string Function { get; init; }
    public required string Location { get; init; }
    /// <summary>Raw bundle URL ("" for native frames) — kept separately from <see cref="Location"/> (which may already be a de-minified file path) so callers can tell first-party from vendor/native without misreading a non-empty "(native)" display string as a URL.</summary>
    public string Url { get; init; } = "";
    public int  TaskCount       { get; init; }
    public long TotalDurationUs { get; init; }
    public long LongestTaskUs   { get; init; }
    public double PctOfTotalBlocking { get; init; }
    public string? CallChain { get; init; }
    /// <summary>Most common possible-trigger location among this group's tasks, when most of them share one — a timing correlation, not a call-tree link.</summary>
    public string? PossibleTriggerFunction { get; init; }
    public string? PossibleTriggerLocation { get; init; }
    /// <summary>Union of every distinct trigger-cluster entry seen across this group's tasks — a broader "everything nearby" view than the single most-common trigger above.</summary>
    public string? PossibleTriggerCluster { get; init; }
}

public sealed class WebLongTaskData
{
    public required string TraceInfo { get; init; }
    public required IReadOnlyList<WebLongTaskRow> Tasks { get; init; }
    public required IReadOnlyList<WebLongTaskRootCause> RootCauses { get; init; }
    public long TotalBlockingTimeUs { get; init; }
    public long LongestTaskUs       { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
}
