using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class ThreadPoolReport
{
    public void Render(ThreadPoolData data, IRenderSink sink)
    {
        RenderThreadPoolState(data, sink);
        if (!data.InfoAvailable) return;
        RenderTaskBreakdown(data, sink);
        if (data.WorkItems.Count > 0) RenderWorkItems(data, sink);
    }

    private static void RenderThreadPoolState(ThreadPoolData data, IRenderSink sink)
    {
        sink.Section("Thread Pool State");
        if (!data.InfoAvailable)
        {
            sink.Alert(AlertLevel.Warning, "ThreadPool information not available in this dump.");
            return;
        }
        sink.KeyValues([
            ("Min worker threads", data.MinThreads!.Value.ToString()),
            ("Max worker threads", data.MaxThreads!.Value.ToString()),
            ("Active workers",     data.ActiveWorkers!.Value.ToString()),
            ("Idle workers",       data.IdleWorkers!.Value.ToString()),
        ]);
        int max = data.MaxThreads!.Value;
        int active = data.ActiveWorkers!.Value;
        int pct = max > 0 ? active * 100 / max : 0;
        if (pct >= 100)
            sink.Alert(AlertLevel.Critical,
                $"Thread pool saturated: {active}/{max} workers ({pct}%)",
                advice: "Avoid synchronous blocking calls (.Result, .Wait(), Thread.Sleep) on thread-pool threads. " +
                        "Use async/await throughout the call chain.");
        else if (pct >= 80)
            sink.Alert(AlertLevel.Warning,
                $"Thread pool near capacity: {active}/{max} workers ({pct}%)");
    }

    private static void RenderTaskBreakdown(ThreadPoolData data, IRenderSink sink)
    {
        int totalTasks = data.TaskStateCounts.Values.Sum();
        if (totalTasks == 0) return;

        sink.Section("Task State Breakdown");
        var rows = data.TaskStateCounts
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new[] { kv.Key, kv.Value.ToString("N0"), $"{kv.Value * 100.0 / totalTasks:F1}%" })
            .ToList();
        sink.Table(["State", "Count", "%"], rows, $"{totalTasks:N0} total Task objects on heap");

        int waitingToRun = data.TaskStateCounts.GetValueOrDefault("WaitingToRun");
        int maxThreads   = data.MaxThreads ?? 1;
        int active       = data.ActiveWorkers ?? 0;

        if (waitingToRun > 1000)
            sink.Alert(AlertLevel.Critical,
                $"{waitingToRun:N0} tasks in WaitingToRun state — thread pool queue backlog.",
                advice: "Reduce synchronous blocking. Consider parallelism limits (SemaphoreSlim). " +
                        "Check for long-running tasks blocking TP threads.");
        else if (waitingToRun > 100)
            sink.Alert(AlertLevel.Warning, $"{waitingToRun:N0} tasks waiting to run.");
        else if (waitingToRun > 0)
        {
            int headroom = Math.Max(maxThreads - active, 1);
            double ratio = (double)waitingToRun / headroom;
            if (ratio > 5.0)
                sink.Alert(AlertLevel.Warning,
                    $"{waitingToRun:N0} queued tasks vs {headroom} idle workers (ratio {ratio:F1}×) — potential burst.",
                    "Queue depth is 5× available workers. A thread-injection delay may create latency spikes.",
                    "Profile with dotnet-trace or PerfView to confirm thread-starvation patterns.");
        }

        int faulted = data.TaskStateCounts.GetValueOrDefault("Faulted");
        if (faulted > 0)
            sink.Alert(AlertLevel.Warning,
                $"{faulted:N0} faulted task(s) on heap — exceptions may be unobserved.",
                advice: "Attach continuations with .ContinueWith or use await to observe Task exceptions. " +
                        "Set TaskScheduler.UnobservedTaskException handler to log them.");
    }

    private static void RenderWorkItems(ThreadPoolData data, IRenderSink sink)
    {
        sink.Section("Queued Work Items");
        var rows = data.WorkItems.OrderByDescending(kv => kv.Value)
            .Select(kv => new[] { kv.Key, kv.Value.ToString("N0") }).ToList();
        sink.Table(["Type", "Count"], rows);
        sink.KeyValues([("Total work items", data.WorkItems.Values.Sum().ToString("N0"))]);
    }
}
