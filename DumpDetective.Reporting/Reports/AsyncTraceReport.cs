using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class AsyncTraceReport
{
    public void Render(AsyncTraceData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "Async/Task analysis — TPL Task lifecycle events showing scheduling rates, execution durations, synchronous blocking patterns, and continuation scheduling hot spots.",
            why: "Synchronous blocking on Task (.Wait()/.Result()) inside async code paths stalls ThreadPool threads, causes starvation, and degrades throughput under load. " +
                 "Continuation storms can also overwhelm the scheduler.",
            impact: "Sync-over-async is one of the leading causes of ThreadPool starvation in ASP.NET Core applications. " +
                    "Each blocked thread consumes stack memory and a ThreadPool slot while performing no useful work.",
            bullets: [
                "Sync-blocking hotspots — call sites where threads blocked synchronously on a Task",
                "Longest tasks        — individual Task executions ordered by elapsed time",
                "Continuation sites   — frames that schedule the most async continuations"
            ],
            action: "Replace .Wait()/.Result()/.GetAwaiter().GetResult() with await. " +
                    "Ensure ConfigureAwait(false) is used in library code to avoid context capture overhead."
        );

        sink.Section("Trace Summary", "async-summary");
        sink.KeyValues([
            ("Trace",                   data.TraceInfo),
            ("Tasks scheduled",         data.TotalTasksScheduled > 0 ? data.TotalTasksScheduled.ToString("N0") : "—"),
            ("Tasks completed",         data.TotalTasksCompleted > 0 ? data.TotalTasksCompleted.ToString("N0") : "—"),
            ("Sync-blocking occurrences", data.SyncBlockingOccurrences > 0 ? data.SyncBlockingOccurrences.ToString("N0") : "0"),
            ("Avg task execution",      data.AvgExecutionMs > 0 ? $"{data.AvgExecutionMs:F2} ms" : "—"),
            ("Max task execution",      data.MaxExecutionMs > 0 ? $"{data.MaxExecutionMs:F1} ms" : "—"),
            ("Process filter",          data.FilteredProcess ?? "(all processes)"),
        ]);

        if (!data.HasData)
        {
            sink.Alert(AlertLevel.Warning, "No async/Task events found in trace.",
                "To capture TPL Task lifecycle events, re-collect with one of the following:",
                "dotnet-trace:\n" +
                "  dotnet trace collect --providers 'Microsoft-Windows-DotNETRuntime:0x40:5' -p <pid>\n\n" +
                "PerfView:\n" +
                "  PerfView.exe /ClrEvents:Tasks,Default /NoGui collect");
            return;
        }

        // ── Alerts ────────────────────────────────────────────────────────────
        if (data.SyncBlockingOccurrences >= 100)
            sink.Alert(AlertLevel.Critical,
                $"Sync-over-async detected: {data.SyncBlockingOccurrences:N0} synchronous Task waits.",
                "Threads are blocking synchronously on Tasks, preventing the ThreadPool from reusing them. " +
                "This is a primary cause of ThreadPool starvation.",
                "Search the codebase for .Wait(), .Result, .GetAwaiter().GetResult() in async code paths.");
        else if (data.SyncBlockingOccurrences >= 10)
            sink.Alert(AlertLevel.Warning,
                $"Synchronous Task blocking detected: {data.SyncBlockingOccurrences:N0} occurrence(s).",
                "Some paths are blocking synchronously on async work. Under load this can cause starvation.",
                "Review sync-blocking hotspots below.");

        if (data.MaxExecutionMs >= 5_000)
            sink.Alert(AlertLevel.Warning,
                $"Long-running Task detected: {data.MaxExecutionMs:F0} ms max execution time.",
                "Tasks running for seconds block their ThreadPool thread for the entire duration.",
                "Break long-running synchronous work into smaller async segments.");

        // ── Schedule rate timeline ─────────────────────────────────────────────
        if (data.ScheduleRateTimeline is { Count: > 2 } tl)
            sink.Sparkline(tl, "Task scheduling rate over time (tasks/second)", "/s");

        // ── Sync-blocking hotspots ─────────────────────────────────────────────
        if (data.SyncBlockingHotspots.Count > 0)
        {
            sink.Section("Sync-Blocking Hotspots (.Wait/.Result)", "async-blocking");
            var rows = new List<string[]>(data.SyncBlockingHotspots.Count);
            foreach (var h in data.SyncBlockingHotspots)
                rows.Add([h.Frame, h.Count.ToString("N0"), $"{h.TotalBlockMs:F1} ms", $"{h.MaxBlockMs:F1} ms"]);
            sink.Table(
                ["Call Site", "Count", "Total Block Time", "Max Block Time"],
                rows,
                $"Top {rows.Count} synchronous blocking sites ordered by total block time");
        }

        // ── Longest tasks ──────────────────────────────────────────────────────
        if (data.LongestTasks.Count > 0)
        {
            sink.Section($"Longest Tasks (top {Math.Min(data.LongestTasks.Count, top)})", "async-longest");
            var rows = new List<string[]>(data.LongestTasks.Count);
            foreach (var t in data.LongestTasks)
                rows.Add([$"#{t.TaskId}", $"{t.ExecutionMs:F1} ms", t.TopFrame]);
            sink.Table(
                ["Task ID", "Execution Time", "Scheduled From"],
                rows,
                "Tasks ordered by execution duration — long execution times indicate CPU-bound or blocking work on pool threads");
        }

        // ── Continuation scheduling hot spots ─────────────────────────────────
        if (data.TopContinuationSites.Count > 0)
        {
            sink.Section("Top Continuation Scheduling Sites", "async-continuations");
            var rows = new List<string[]>(data.TopContinuationSites.Count);
            foreach (var c in data.TopContinuationSites)
                rows.Add([c.Frame, c.Count.ToString("N0")]);
            sink.Table(
                ["Frame", "Continuations Scheduled"],
                rows,
                "High continuation counts are expected at throughput hot paths; extreme values may indicate continuation storms");
        }
    }
}
