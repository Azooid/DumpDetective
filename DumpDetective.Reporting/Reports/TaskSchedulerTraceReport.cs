using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class TaskSchedulerTraceReport
{
    public void Render(TaskSchedulerTraceData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "Task Scheduler analysis from TaskScheduled, Task/Execute, and TaskWait events — identifies long-running tasks, excessive task wait depth, and cancellation patterns.",
            why: "Tasks that run longer than expected block thread pool threads, reduce throughput, and cascade into thread starvation when many tasks queue behind them.",
            impact: "A single CPU-bound task holding a ThreadPool thread for 10+ seconds can starve dozens of I/O-bound continuations waiting to complete.",
            bullets: [
                "Long-running tasks  — tasks exceeding 5 seconds of execution time",
                "Task wait depth     — tasks blocked waiting on other tasks",
                "Cancelled tasks     — tasks that were cancelled before completion"
            ],
            action: "Move CPU-bound work to dedicated threads (Task.Factory.StartNew with LongRunning). " +
                    "Use CancellationToken throughout async call chains. " +
                    "Avoid Task.Wait() and Task.Result — prefer await."
        );

        sink.Section("Trace Summary", "task-summary");
        sink.KeyValues([
            ("Trace",               data.TraceInfo),
            ("Total scheduled",     data.TotalScheduled.ToString("N0")),
            ("Total completed",     data.TotalCompleted.ToString("N0")),
            ("Total cancelled",     data.TotalCancelled.ToString("N0")),
            ("Long-running tasks",  data.LongRunningTaskCount > 0
                                     ? $"{data.LongRunningTaskCount} ⚠" : "0"),
            ("Max task duration",   $"{data.MaxTaskDurationMs:F0} ms"),
            ("Avg task duration",   $"{data.AvgTaskDurationMs:F1} ms"),
            ("Max wait time",       $"{data.MaxWaitMs:F0} ms"),
            ("Process filter",      data.FilteredProcess ?? "(all processes)"),
        ]);

        if (!data.HasData)
        {
            sink.Alert(AlertLevel.Info, "No Task Scheduler events found.",
                "Collect with: --providers 'Microsoft-Windows-DotNETRuntime:0x40:4' (ThreadingKeyword)");
            return;
        }

        if (data.LongRunningTaskCount > 0)
            sink.Alert(AlertLevel.Warning,
                $"{data.LongRunningTaskCount} long-running task(s) detected (>{5000} ms).",
                "Tasks holding thread pool threads block other scheduled work.",
                "Use TaskCreationOptions.LongRunning to allocate a dedicated thread for long tasks.");

        if (data.ScheduledTimeline is { Count: > 2 } tl)
            sink.Sparkline(tl, "Tasks scheduled per second", "/s");

        if (data.LongRunningTasks.Count > 0)
        {
            sink.Section("Long-Running Tasks", "task-long");
            var rows = new List<string[]>(Math.Min(top, data.LongRunningTasks.Count));
            foreach (var t in data.LongRunningTasks.Take(top))
                rows.Add([$"Task {t.TaskId}", $"{t.DurationMs:F0} ms",
                           $"{t.ScheduledMs:F0} ms", t.TopFrame]);
            sink.Table(
                ["Task ID", "Duration", "Scheduled At", "Creating Method"],
                rows, "Tasks ordered by duration descending");
        }
    }
}
