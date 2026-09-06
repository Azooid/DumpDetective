using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class WebLongTaskReport
{
    public void Render(WebLongTaskData data, IRenderSink sink)
    {
        sink.Section("Long Tasks");
        sink.Explain(
            what: "Main-thread tasks (RunTask spans) that ran for 50ms or longer — the Long Tasks API threshold. " +
                  "Below this, work is invisible to the user; above it, the page can't respond to input, scroll, or paint. " +
                  "Each task is automatically correlated against the CPU profiler data overlapping its window — no " +
                  "need to cross-reference timestamps by hand.",
            why:  "A single long task freezes the page for its entire duration. Frequent long tasks — even short " +
                  "individually — add up to a page that feels consistently janky rather than momentarily frozen.",
            bullets:
            [
                "Start with Root Causes below, not the raw task list — it tells you what to fix, ranked by how much " +
                  "blocked time fixing it would remove",
                "A single task > 500ms → a user-visible freeze — break it up or move it off the main thread",
                "'—' Likely Cause → no CPU profile samples overlapped that task's window (rare, e.g. idle/native-only tasks)",
            ],
            action: "Fix the top row of Root Causes first — it removes more blocking time than any other single change.");

        sink.KeyValues([
            ("Tasks ≥ 50ms",           data.Tasks.Count.ToString("N0")),
            ("Total blocking time",    $"{data.TotalBlockingTimeUs / 1000.0:F0} ms"),
            ("Longest single task",    $"{data.LongestTaskUs / 1000.0:F0} ms"),
        ]);

        RenderRootCauses(data, sink);

        if (data.Tasks.Count > 0)
        {
            sink.Section("All Long Tasks");
            sink.Table(
                ["Duration", "Timestamp (trace-relative)", "Likely Cause", "Location"],
                data.Tasks.Take(50).Select(t => new[]
                {
                    $"{t.DurationUs / 1000.0:F1} ms",
                    $"{t.TimestampUs / 1000.0:F0} ms",
                    t.AttributedFunction ?? "—",
                    t.AttributedLocation,
                }).ToList(),
                "Ranked by duration descending, top 50 shown. Likely Cause is the CPU-profiler-sampled function with " +
                "the most self-time inside this task's window — see \"Your Code Seen Nearby\" below for what your " +
                "own code was doing right before each one.");

            RenderNearbyApplicationCode(data, sink);
        }

        sink.Section("Findings");
        foreach (var f in data.Findings)
            sink.Alert(
                f.Severity switch
                {
                    FindingSeverity.Critical => AlertLevel.Critical,
                    FindingSeverity.Warning  => AlertLevel.Warning,
                    _                        => AlertLevel.Info,
                },
                f.Headline, f.Detail, f.Advice);
    }

    private static void RenderRootCauses(WebLongTaskData data, IRenderSink sink)
    {
        if (data.RootCauses.Count == 0) return;

        sink.Section("Root Causes");
        sink.Explain(
            what: "Every long task rolled up by attributed cause, ranked by total blocked time — the 'fix this one " +
                  "thing' view. Three 2-second freezes from one function outrank ten 60ms ones from another, so this " +
                  "is sorted by time removed, not how many tasks share the cause.",
            action: "Fixing the top row removes more blocking time than any other single change would.");

        sink.Table(
            ["% of Blocking Time", "Total Time", "Tasks", "Longest", "Function", "Call Stack"],
            data.RootCauses.Take(15).Select(rc =>
            {
                var chain = WebCallChainHelper.BuildChain(rc.Function, rc.Location, rc.Url, rc.TaskCount, rc.PctOfTotalBlocking, rc.CallChain);
                var stack = WebCallChainHelper.FormatStackText(chain);
                if (rc.PossibleTriggerCluster is not null)
                    stack = WebCallChainHelper.AppendPossibleTrigger(stack, rc.PossibleTriggerCluster);
                return new[]
                {
                    $"{rc.PctOfTotalBlocking:F0}%",
                    $"{rc.TotalDurationUs / 1000.0:F0} ms",
                    rc.TaskCount.ToString("N0"),
                    $"{rc.LongestTaskUs / 1000.0:F0} ms",
                    rc.Function,
                    stack,
                };
            }).ToList(),
            "Ranked by total blocked time descending, top 15 shown. Call Stack reads like a .NET stack trace — the " +
            "cause itself is the first line, each line below it is the next caller up; ★ marks where your own code " +
            "enters the stack. If a chain has none, check the '~ your code seen running nearby' line before " +
            "assuming the fix isn't in your code (a timing correlation, not a call-tree fact — the only signal that " +
            "survives a setTimeout/promise boundary the CPU profiler can't trace through).");
    }

    private static void RenderNearbyApplicationCode(WebLongTaskData data, IRenderSink sink)
    {
        var withCluster = data.Tasks.Where(t => t.PossibleTriggerCluster is not null).ToList();
        if (withCluster.Count == 0) return;

        // Many tasks in the same burst share the identical set of nearby first-party
        // functions — one mini-table per task (up to 50) would just repeat itself.
        // Grouped by distinct cluster content instead: each unique "burst" shown once,
        // with how many tasks and how much blocked time it's associated with.
        var grouped = withCluster
            .GroupBy(t => t.PossibleTriggerCluster)
            .Select(g => new { Cluster = g.Key!, TaskCount = g.Count(), TotalDurationUs = g.Sum(t => t.DurationUs) })
            .OrderByDescending(g => g.TotalDurationUs)
            .Take(10)
            .ToList();

        sink.BeginDetails("Your Code Seen Nearby — distinct bursts across all tasks", open: false);
        sink.Explain(
            what: "Every distinct set of first-party functions seen running in the second before a task started, " +
                  "grouped — many tasks in the same burst share the identical nearby code, so each unique set is " +
                  "shown once rather than once per task.",
            action: "Useful when Likely Cause in the tables above is vendor code that only schedules the real work asynchronously.");
        foreach (var g in grouped)
            RenderNearbyTable(
                $"Seen before {g.TaskCount} task(s), {g.TotalDurationUs / 1000.0:F0} ms total blocked time",
                g.Cluster, sink);
        sink.EndDetails();
    }

    private static void RenderNearbyTable(string heading, string cluster, IRenderSink sink)
    {
        sink.Text(heading);
        sink.Table(["Your code seen running nearby"],
            cluster.Split(", ").Select(entry => new[] { entry }).ToList());
    }
}
