using DumpDetective.Core;
using DumpDetective.Output;

using Spectre.Console;

namespace DumpDetective.Commands;

// Reports thread pool worker saturation, Task state distribution
// (WaitingToRun/Running/Faulted/etc.), and queued work item counts
// from a single heap walk.
internal static class ThreadPoolCommand
{
    private const string Help = """
        Usage: DumpDetective thread-pool <dump-file> [options]

        Options:
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help
        """;

    // TASK_STATE_* flag constants from the .NET runtime source
    private const int TASK_STATE_RAN_TO_COMPLETION = 0x1000000;
    private const int TASK_STATE_WAITING_ON_CHILDREN = 0x0800000;
    private const int TASK_STATE_CANCELED = 0x0400000;
    private const int TASK_STATE_FAULTED = 0x0200000;
    private const int TASK_STATE_DELEGATE_INVOKED = 0x0080000;
    private const int TASK_STATE_STARTED = 0x0010000;
    private const int TASK_STATE_WAITING_FOR_ACTIVATION = 0x0001000;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);
        sink.Header(
            "Dump Detective — Thread Pool Analysis",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        var tp = ctx.Runtime.ThreadPool;
        RenderThreadPoolState(sink, tp);

        if (tp is null || !ctx.Heap.CanWalkHeap) return;

        var (taskStateCounts, workItems) = ScanTasksAndWorkItems(ctx);
        RenderTaskBreakdown(sink, taskStateCounts, tp);
        if (workItems.Count > 0) RenderWorkItems(sink, workItems);
    }

    // ── Data gathering ────────────────────────────────────────────────────────

    // Single heap walk collecting Task state counts and non-Task work item counts.
    static (Dictionary<string, int> TaskStateCounts, Dictionary<string, int> WorkItems)
        ScanTasksAndWorkItems(DumpContext ctx)
    {
        var taskStateCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["WaitingToRun"] = 0,
            ["Running"] = 0,
            ["WaitingForActivation"] = 0,
            ["RanToCompletion"] = 0,
            ["Faulted"] = 0,
            ["Canceled"] = 0,
            ["Other"] = 0,
        };
        var workItems = new Dictionary<string, int>(StringComparer.Ordinal);

        CommandBase.RunStatus("Scanning work items and tasks...", () =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                var name = obj.Type.Name ?? string.Empty;

                if (IsTask(name))
                {
                    string stateLabel = GetTaskStateLabel(obj);
                    taskStateCounts[stateLabel] = taskStateCounts.GetValueOrDefault(stateLabel) + 1;
                }
                else if (IsWorkItem(name))
                {
                    workItems[name] = workItems.GetValueOrDefault(name) + 1;
                }
            }
        });
        return (taskStateCounts, workItems);
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    // Thread pool min/max/active/idle key-values + saturation alerts.
    static void RenderThreadPoolState(IRenderSink sink, Microsoft.Diagnostics.Runtime.ClrThreadPool? tp)
    {
        sink.Section("Thread Pool State");
        if (tp is null)
        {
            sink.Alert(AlertLevel.Warning, "ThreadPool information not available in this dump.");
            return;
        }
        sink.KeyValues([
            ("Min worker threads",  tp.MinThreads.ToString()),
            ("Max worker threads",  tp.MaxThreads.ToString()),
            ("Active workers",      tp.ActiveWorkerThreads.ToString()),
            ("Idle workers",        tp.IdleWorkerThreads.ToString()),
        ]);
        int pct = tp.MaxThreads > 0 ? tp.ActiveWorkerThreads * 100 / tp.MaxThreads : 0;
        if (pct >= 100)
            sink.Alert(AlertLevel.Critical,
                $"Thread pool saturated: {tp.ActiveWorkerThreads}/{tp.MaxThreads} workers ({pct}%)",
                advice: "Avoid synchronous blocking calls (.Result, .Wait(), Thread.Sleep) on thread-pool threads. " +
                        "Use async/await throughout the call chain.");
        else if (pct >= 80)
            sink.Alert(AlertLevel.Warning,
                $"Thread pool near capacity: {tp.ActiveWorkerThreads}/{tp.MaxThreads} workers ({pct}%)");
    }

    // Task state frequency table + WaitingToRun backlog and Faulted alerts.
    static void RenderTaskBreakdown(IRenderSink sink,
        Dictionary<string, int> taskStateCounts,
        Microsoft.Diagnostics.Runtime.ClrThreadPool tp)
    {
        int totalTasks = taskStateCounts.Values.Sum();
        if (totalTasks == 0) return;
        sink.Section("Task State Breakdown");
        var taskRows = taskStateCounts
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new[] { kv.Key, kv.Value.ToString("N0"), $"{kv.Value * 100.0 / totalTasks:F1}%" })
            .ToList();
        sink.Table(["State", "Count", "%"], taskRows, $"{totalTasks:N0} total Task objects on heap");

        int waitingToRun = taskStateCounts["WaitingToRun"];
        if (waitingToRun > 1000)
            sink.Alert(AlertLevel.Critical,
                $"{waitingToRun:N0} tasks in WaitingToRun state — thread pool queue backlog.",
                advice: "Reduce synchronous blocking. Consider parallelism limits (SemaphoreSlim). " +
                        "Check for long-running tasks blocking TP threads.");
        else if (waitingToRun > 100)
            sink.Alert(AlertLevel.Warning, $"{waitingToRun:N0} tasks waiting to run.");
        else if (waitingToRun > 0)
        {
            int headroom = Math.Max(tp.MaxThreads - tp.ActiveWorkerThreads, 1);
            double ratio = (double)waitingToRun / headroom;
            if (ratio > 5.0)
                sink.Alert(AlertLevel.Warning,
                    $"{waitingToRun:N0} queued tasks vs {headroom} idle workers (ratio {ratio:F1}×) — potential burst.",
                    "Queue depth is 5× available workers. A thread-injection delay may create latency spikes.",
                    "Profile with dotnet-trace or PerfView to confirm thread-starvation patterns.");
        }

        int faulted = taskStateCounts["Faulted"];
        if (faulted > 0)
            sink.Alert(AlertLevel.Warning,
                $"{faulted:N0} faulted task(s) on heap — exceptions may be unobserved.",
                advice: "Attach continuations with .ContinueWith or use await to observe Task exceptions. " +
                        "Set TaskScheduler.UnobservedTaskException handler to log them.");
    }

    // Non-Task work item frequency table.
    static void RenderWorkItems(IRenderSink sink, Dictionary<string, int> workItems)
    {
        sink.Section("Queued Work Items");
        var workRows = workItems.OrderByDescending(kv => kv.Value)
            .Select(kv => new[] { kv.Key, kv.Value.ToString("N0") }).ToList();
        sink.Table(["Type", "Count"], workRows);
        sink.KeyValues([("Total work items", workItems.Values.Sum().ToString("N0"))]);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Returns true for System.Threading.Tasks.Task and Task<T> type names.
    static bool IsTask(string typeName) =>
        typeName == "System.Threading.Tasks.Task" ||
        typeName.StartsWith("System.Threading.Tasks.Task<", StringComparison.Ordinal) ||
        typeName.StartsWith("System.Threading.Tasks.Task`", StringComparison.Ordinal);

    // Returns true for QueueUserWorkItemCallback and types whose names contain WorkItem/WorkRequest.
    static bool IsWorkItem(string typeName) =>
        typeName is "System.Threading.QueueUserWorkItemCallback" or
                    "System.Threading.QueueUserWorkItemCallbackDefaultContext" ||
        typeName.Contains("WorkItem", StringComparison.OrdinalIgnoreCase) ||
        typeName.Contains("WorkRequest", StringComparison.OrdinalIgnoreCase);

    // Decodes m_stateFlags into a human-readable Task lifecycle label using the runtime
    // flag constants declared at the top of the class.
    static string GetTaskStateLabel(Microsoft.Diagnostics.Runtime.ClrObject obj)
    {
        try
        {
            int flags = obj.ReadField<int>("m_stateFlags");
            if ((flags & TASK_STATE_FAULTED) != 0) return "Faulted";
            if ((flags & TASK_STATE_CANCELED) != 0) return "Canceled";
            if ((flags & TASK_STATE_RAN_TO_COMPLETION) != 0) return "RanToCompletion";
            if ((flags & TASK_STATE_DELEGATE_INVOKED) != 0) return "Running";
            if ((flags & TASK_STATE_STARTED) != 0) return "WaitingToRun";
            if ((flags & TASK_STATE_WAITING_FOR_ACTIVATION) != 0) return "WaitingForActivation";
            return "Other";
        }
        catch { return "Other"; }
    }
}
