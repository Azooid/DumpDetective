using DumpDetective.Core.Models;

namespace DumpDetective.Core.Models.CommandData;

public sealed class WebCpuHotspotRow
{
    public required string Url          { get; init; }
    public required string FunctionName { get; init; }
    public int    Line        { get; init; }
    public long   SelfTimeUs  { get; init; }
    public long   SampleCount { get; init; }
    public double PctOfTotal  { get; init; }

    /// <summary>De-minified source file/line resolved via the trace's embedded source maps, when available.</summary>
    public string? ResolvedFile { get; init; }
    public int     ResolvedLine { get; init; } = -1;

    /// <summary>Nearest callers, nearest first — often more useful than the leaf function's own name for identifying which feature is responsible (a shared utility is called from everywhere; its callers aren't).</summary>
    public string? CallChain { get; init; }

    /// <summary>The nearest first-party function in <see cref="CallChain"/>, if any — the bridge between a vendor hotspot and the application code that (synchronously) called into it.</summary>
    public string? AppCallerFunction { get; init; }
    public string? AppCallerLocation { get; init; }

    /// <summary>
    /// First-party functions seen running shortly before the long tasks this hotspot was
    /// the attributed cause of — a timing correlation, not a call-tree fact, carried over
    /// from <c>web-long-tasks</c>' "Your Code Seen Nearby" (same underlying
    /// <c>WebLongTask.PossibleTriggerCluster</c> data, joined here by hotspot identity so
    /// it doesn't take cross-referencing a second report to see it). Populated only when
    /// this hotspot has no synchronous application caller at all — when one exists,
    /// <see cref="AppCallerFunction"/> is the stronger, proven link.
    /// </summary>
    public string? PossibleTriggerCluster { get; init; }

    /// <summary>"file.ts:42" when resolved, else the minified bundle "url:line" as recorded by the profiler.</summary>
    public string Location => ResolvedFile is not null
        ? $"{ResolvedFile}:{ResolvedLine}"
        : Url.Length > 0 ? $"{Url}:{Line}" : "(native)";

    /// <summary>
    /// True for first-party application source (dev-server/app paths), false for
    /// vendor/library code (<c>node_modules</c>, bundler-prefixed vendor chunks) or
    /// native frames. A thin application function that only <em>schedules</em> expensive
    /// work (e.g. via <c>setTimeout</c>) can have negligible self-time and never appear
    /// in the top self-time ranking at all — this flag is what lets a report list every
    /// first-party function regardless of rank, so that case isn't invisible.
    /// </summary>
    public bool IsApplicationCode =>
        Url.Length > 0 &&
        !Url.Contains("node_modules", StringComparison.OrdinalIgnoreCase) &&
        !Url.Contains("/.vite/deps/", StringComparison.OrdinalIgnoreCase) &&
        !Url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase);
}

public sealed class WebCpuHotspotData
{
    public required string TraceInfo { get; init; }
    public long   TotalSampledUs { get; init; }
    public required IReadOnlyList<WebCpuHotspotRow> Top { get; init; }
    /// <summary>Every first-party (application) hotspot found anywhere in the profile, regardless of self-time rank — see <see cref="WebCpuHotspotRow.IsApplicationCode"/>.</summary>
    public required IReadOnlyList<WebCpuHotspotRow> ApplicationCode { get; init; }
    /// <summary>
    /// Vendor hotspots whose own name isn't first-party but whose caller chain reaches
    /// back into application code — the explicit bridge between "Called From" and
    /// "Application Code" for the (common) case where the two would otherwise look
    /// unconnected: the top hotspots by self-time often have no application caller within
    /// reach, while plenty further down the list do.
    /// </summary>
    public required IReadOnlyList<WebCpuHotspotRow> LinkedToApplicationCode { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
}
