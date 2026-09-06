namespace DumpDetective.Analysis.WebTrace.Model;

/// <summary>
/// One <c>UpdateCounters</c> sample — periodic snapshot of JS heap / DOM state
/// emitted by DevTools while recording.
/// </summary>
public readonly record struct WebCounterSample(
    long TimestampUs,
    long JsHeapSizeUsed,
    int  Documents,
    int  Nodes,
    int  JsEventListeners);

/// <summary>
/// One unique call-frame from the V8 CPU profiler, aggregated across every
/// <c>ProfileChunk</c> sample that hit it. Keyed by (Url, FunctionName, Line)
/// — not by V8's internal node id, since the same source location can appear
/// under multiple node ids (different call paths) and we want one row per
/// "place in the code", not per call-tree path.
/// </summary>
public sealed class WebCpuHotspot
{
    public required string Url          { get; init; }
    public required string FunctionName { get; init; }
    public int             Line         { get; init; }
    public long            SelfTimeUs   { get; set; }
    public long            SampleCount  { get; set; }

    /// <summary>De-minified source file, resolved via the trace's embedded source maps — null when unavailable.</summary>
    public string? ResolvedFile { get; init; }
    /// <summary>De-minified line number; -1 when <see cref="ResolvedFile"/> is null.</summary>
    public int     ResolvedLine { get; init; } = -1;

    /// <summary>
    /// Nearest callers, nearest first (e.g. "getWidth ← elementSize ← _moveSeparator") —
    /// walked from the V8 CPU profiler's node.parent chain once, when this hotspot is
    /// first seen. A leaf function's own name is rarely enough to tell whether it belongs
    /// to a specific feature (a shared utility like "getCSSProperty" is called from
    /// everywhere); the immediate callers are what actually reveal that, e.g. a grid's
    /// column-resize handler several frames up a size-measurement utility's call chain.
    /// </summary>
    public string? CallChain { get; init; }
}

/// <summary>
/// A main-thread task (<c>RunTask</c> / long "X"-phase span) over the reporting floor.
/// <c>Attributed*</c> fields are filled in after the streaming pass, by correlating the
/// task's time window against the bucketed CPU self-time recorded alongside the hotspot
/// aggregation (see <c>ChromeTraceParser.AttributeLongTasks</c>) — turning an opaque
/// "something ran for 800ms here" row into "here's what was actually running".
/// </summary>
public sealed class WebLongTask
{
    public required long TimestampUs { get; init; }
    public required long DurationUs  { get; init; }
    public int Pid { get; init; }
    public int Tid { get; init; }

    public string? AttributedFunction    { get; set; }
    public string? AttributedUrl         { get; set; }
    public int     AttributedLine        { get; set; } = -1;
    public string? AttributedResolvedFile { get; set; }
    public int     AttributedResolvedLine { get; set; } = -1;
    /// <summary>Self-time the attributed function accrued within the buckets this task overlaps (approximate — bucket-granularity, not exact task-window).</summary>
    public long    AttributedSelfTimeUs  { get; set; }
    /// <summary>Same call-chain breadcrumb as <see cref="WebCpuHotspot.CallChain"/>, carried over from the attributed hotspot.</summary>
    public string? AttributedCallChain   { get; set; }

    // ── Possible trigger — a timing correlation, not a call-tree link ──────────────────
    // V8's CPU profiler only tracks the synchronous call stack; a function that schedules
    // work via setTimeout/a promise and returns immediately has no call-tree link to the
    // deferred work it caused. This is filled in by ChromeTraceParser.AttributePossibleTriggers:
    // the nearest first-party (application) sample seen before this task started, within a
    // bounded lookback window — a *candidate* explanation surfaced explicitly as such, not
    // a proven cause.
    public string? PossibleTriggerFunction     { get; set; }
    public string? PossibleTriggerUrl          { get; set; }
    public int     PossibleTriggerLine         { get; set; } = -1;
    public string? PossibleTriggerResolvedFile { get; set; }
    public int     PossibleTriggerResolvedLine { get; set; } = -1;
    /// <summary>Time between the candidate trigger sample and this task's start.</summary>
    public long    PossibleTriggerGapUs        { get; set; }

    /// <summary>
    /// Every distinct first-party function seen running in the second immediately before
    /// this task started (not just the single nearest one) — a burst of setup/render code
    /// (e.g. several grid-related components firing together) often shows up here as a
    /// group, which is more informative than picking just one when several are involved.
    /// Formatted as "Function (file.ts:N)" entries, most recent first.
    /// </summary>
    public string? PossibleTriggerCluster { get; set; }
}

/// <summary>A GC pause span reconstructed from begin/end events in the v8.gc category.</summary>
public readonly record struct WebGcEvent(
    long   TimestampUs,
    long   DurationUs,
    string Kind); // "Major" / "Minor" / "Scavenge" etc — best-effort from the event name.

/// <summary>
/// One decoded DevTools screenshot frame (<c>Screenshot</c> event) — raw JPEG bytes plus
/// its trace timestamp. Kept base64-encoded exactly as recorded; the filmstrip renderer
/// embeds it straight into the report as a data URI, so no re-encoding is ever needed.
/// </summary>
public readonly record struct WebScreenshotFrame(long TimestampUs, string Base64Jpeg);

/// <summary>
/// One completed network request, correlated across <c>ResourceSendRequest</c> /
/// <c>ResourceReceiveResponse</c> / <c>ResourceFinish</c> by <c>requestId</c>. Only
/// requests that reached <c>ResourceFinish</c> before the recording ended are included —
/// still-in-flight requests are dropped rather than reported with a misleading duration.
/// </summary>
public sealed class WebNetworkRequest
{
    public required string Url          { get; init; }
    public string?         Method       { get; init; }
    public string?         ResourceType { get; init; }
    public long            SendTs       { get; init; }
    public long            ResponseTs   { get; init; } // 0 when no ResourceReceiveResponse was seen
    public long            FinishTs     { get; init; }
    public long            EncodedBytes { get; init; }
    public long            DecodedBytes { get; init; }
    public bool            Failed       { get; init; }
    public bool            FromCache    { get; init; }

    public long DurationUs      => SendTs > 0 && FinishTs > SendTs ? FinishTs - SendTs : 0;
    public long TimeToFirstByteUs => SendTs > 0 && ResponseTs > SendTs ? ResponseTs - SendTs : 0;
}

/// <summary>Mutable in-flight accumulator while a request's Send/Receive/Finish events are being correlated.</summary>
internal sealed class WebNetworkRequestBuilder
{
    public string  Url          = "";
    public string? Method;
    public string? ResourceType;
    public long    SendTs;
    public long    ResponseTs;
    public bool    FromCache;
}

/// <summary>
/// Everything the parser extracted from one trace file, already reduced to the
/// minimum shape analyzers need — never the raw event array.
/// </summary>
public sealed class WebTraceData
{
    public required string                          SourcePath      { get; init; }
    public required IReadOnlyList<WebCounterSample>  Counters        { get; init; }
    public required IReadOnlyList<WebCpuHotspot>     CpuHotspots     { get; init; }
    public required IReadOnlyList<WebLongTask>       LongTasks       { get; init; }
    public required IReadOnlyList<WebGcEvent>        GcEvents        { get; init; }
    public required IReadOnlyList<WebScreenshotFrame> Screenshots    { get; init; }
    public required IReadOnlyList<WebNetworkRequest>  NetworkRequests { get; init; }

    /// <summary>Total <c>BeginFrame</c> events — one per frame the compositor attempted to produce.</summary>
    public int BeginFrameCount   { get; init; }
    /// <summary>Total <c>DroppedFrame</c> events — frames that were scheduled but never presented.</summary>
    public int DroppedFrameCount { get; init; }
    /// <summary>Completed <c>InputLatency::*</c> durations, in microseconds (async begin→end pairs only).</summary>
    public required IReadOnlyList<long> InputLatenciesUs { get; init; }

    /// <summary>Total events seen while streaming (diagnostic only — not retained data).</summary>
    public long TotalEventsScanned { get; init; }

    /// <summary>Trace duration derived from first/last timestamp seen, in microseconds.</summary>
    public long DurationUs { get; init; }
}
