using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Tracing;

namespace DumpDetective.Analysis.Trace;

/// <summary>
/// Builds a unified <see cref="TimelineSlice"/> list from the outputs of all
/// trace sub-analyzers. Each analyzer contributes its significant events as
/// slices; the result is a chronologically sorted cross-analyzer timeline.
///
/// Used by <c>TraceAnalyzeCommand</c> to render the "Timeline Correlation" section
/// at the top of the combined report.
/// </summary>
public static class TraceTimelineBuilder
{
    /// <summary>
    /// Extracts and merges timeline slices from all analyzer outputs.
    /// </summary>
    /// <param name="maxSlices">
    /// Maximum number of slices to return. If the combined event set is larger,
    /// slices are filtered by severity (Critical &gt; Warning &gt; Info) and then
    /// by duration/prominence descending.
    /// </param>
    public static IReadOnlyList<TimelineSlice> Build(
        GcTraceData?              gc,
        ContentionTraceData?      contention,
        ExceptionsTraceData?      exceptions,
        HttpTraceData?            http,
        SqlTraceData?             sql,
        AsyncTraceData?           async_,
        int maxSlices = 200)
    {
        var slices = new List<TimelineSlice>(256);

        AddGcSlices(slices, gc);
        AddContentionSlices(slices, contention);
        AddExceptionSlices(slices, exceptions);
        AddHttpSlices(slices, http);
        AddSqlSlices(slices, sql);
        AddAsyncSlices(slices, async_);

        // Sort chronologically
        slices.Sort(static (a, b) => a.StartMs.CompareTo(b.StartMs));

        // Trim to maxSlices — keep Critical first, then Warning, then Info; within same severity by duration desc
        if (slices.Count > maxSlices)
        {
            slices.Sort(static (a, b) =>
            {
                int sev = b.Severity.CompareTo(a.Severity);
                return sev != 0 ? sev : b.DurationMs.CompareTo(a.DurationMs);
            });
            slices.RemoveRange(maxSlices, slices.Count - maxSlices);
            // Re-sort chronologically after pruning
            slices.Sort(static (a, b) => a.StartMs.CompareTo(b.StartMs));
        }

        return slices;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Per-analyzer slice extractors
    // ─────────────────────────────────────────────────────────────────────────

    private static void AddGcSlices(List<TimelineSlice> slices, GcTraceData? gc)
    {
        if (gc is null) return;
        foreach (var ev in gc.Events)
        {
            if (ev.PauseMs <= 1) continue;  // skip sub-ms entries
            var sev = ev.PauseMs >= 200 ? FindingSeverity.Critical
                    : ev.PauseMs >= 50  ? FindingSeverity.Warning
                    : FindingSeverity.Info;
            slices.Add(new TimelineSlice(
                ev.TimeMs, ev.TimeMs + ev.PauseMs,
                "GC", $"Gen{ev.Generation} {ev.Type} {ev.PauseMs:F0} ms ({ev.Reason})",
                sev));
        }
    }

    private static void AddContentionSlices(List<TimelineSlice> slices, ContentionTraceData? ct)
    {
        if (ct is null) return;
        foreach (var ev in ct.Events)
        {
            if (ev.WaitMs < 5) continue;
            var sev = ev.WaitMs >= 500 ? FindingSeverity.Critical
                    : ev.WaitMs >= 50  ? FindingSeverity.Warning
                    : FindingSeverity.Info;
            slices.Add(new TimelineSlice(
                ev.TimeMs, ev.TimeMs + ev.WaitMs,
                "Contention", $"Lock {ev.WaitMs:F0} ms — {Trim(ev.TopFrame, 60)}",
                sev));
        }
    }

    private static void AddExceptionSlices(List<TimelineSlice> slices, ExceptionsTraceData? ex)
    {
        if (ex is null) return;
        // Point events — use a 1 ms width so they appear on the timeline
        foreach (var ev in ex.RecentEvents)
        {
            slices.Add(new TimelineSlice(
                ev.TimeMs, ev.TimeMs + 1,
                "Exception", $"{ev.ExceptionType} — {Trim(ev.Message, 60)}",
                FindingSeverity.Warning));
        }
    }

    private static void AddHttpSlices(List<TimelineSlice> slices, HttpTraceData? http)
    {
        if (http is null || !http.HasData) return;
        foreach (var req in http.SlowRequests)
        {
            var sev = req.DurationMs >= 5_000 ? FindingSeverity.Critical
                    : req.DurationMs >= 2_000 ? FindingSeverity.Warning
                    : FindingSeverity.Info;
            slices.Add(new TimelineSlice(
                req.StartTimeMs, req.StartTimeMs + req.DurationMs,
                "HTTP", $"{req.Method} {Trim(req.Path, 60)} {req.DurationMs:F0} ms [{req.StatusCode}]",
                sev));
        }
    }

    private static void AddSqlSlices(List<TimelineSlice> slices, SqlTraceData? sql)
    {
        if (sql is null || !sql.HasData) return;
        foreach (var cmd in sql.SlowCommands)
        {
            var sev = cmd.DurationMs >= 5_000 ? FindingSeverity.Critical
                    : cmd.DurationMs >= 1_000 ? FindingSeverity.Warning
                    : FindingSeverity.Info;
            slices.Add(new TimelineSlice(
                cmd.StartTimeMs, cmd.StartTimeMs + cmd.DurationMs,
                "SQL", $"{cmd.Database} {cmd.DurationMs:F0} ms — {Trim(cmd.CommandText, 60)}",
                sev));
        }
    }

    private static void AddAsyncSlices(List<TimelineSlice> slices, AsyncTraceData? async_)
    {
        if (async_ is null || !async_.HasData) return;
        foreach (var t in async_.LongestTasks)
        {
            if (t.ExecutionMs < 100) continue;
            var sev = t.ExecutionMs >= 5_000 ? FindingSeverity.Critical
                    : t.ExecutionMs >= 1_000 ? FindingSeverity.Warning
                    : FindingSeverity.Info;
            slices.Add(new TimelineSlice(
                t.ScheduledTimeMs, t.ScheduledTimeMs + t.ExecutionMs,
                "Async", $"Task #{t.TaskId} {t.ExecutionMs:F0} ms — {Trim(t.TopFrame, 60)}",
                sev));
        }
    }

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
