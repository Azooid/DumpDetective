using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Analysis.Consumers;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Reports thread-pool health: worker/completion-port thread counts, Task state
/// distribution, and queued work-item type counts.
/// Fast path: reads the pre-built <see cref="Consumers.ThreadPoolConsumerCache"/> from
///   <c>CollectHeapObjectsCombined</c> — no second heap walk.
/// Slow path: walks the heap classifying Task objects by <c>m_stateFlags</c> bit fields
///   using the same internal flag constants as the .NET runtime.
/// Thread-pool counters (min/max/active/idle) are read directly from
///   <c>ctx.Runtime.ThreadPool</c> — these are always live regardless of the path taken.
/// </summary>
public sealed class ThreadPoolAnalyzer
{
    private const int TASK_STATE_RAN_TO_COMPLETION      = 0x1000000;
    private const int TASK_STATE_CANCELED               = 0x0400000;
    private const int TASK_STATE_FAULTED                = 0x0200000;
    private const int TASK_STATE_DELEGATE_INVOKED       = 0x0080000;
    private const int TASK_STATE_STARTED                = 0x0010000;
    private const int TASK_STATE_WAITING_FOR_ACTIVATION = 0x0001000;

    public ThreadPoolData Analyze(DumpContext ctx)
    {
        var tp = ctx.Runtime.ThreadPool;

        if (tp is null)
            return new ThreadPoolData(null, null, null, null, false,
                new Dictionary<string, int>(), new Dictionary<string, int>());

        // Fast path: pre-populated by ThreadPoolConsumer during CollectHeapObjectsCombined.
        var cached = ctx.GetAnalysis<Consumers.ThreadPoolConsumerCache>();
        if (cached is not null)
        {
            return new ThreadPoolData(
                tp.MinThreads, tp.MaxThreads,
                tp.ActiveWorkerThreads, tp.IdleWorkerThreads,
                true, cached.TaskCounts, cached.WorkItems);
        }

        var taskCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["WaitingToRun"]         = 0,
            ["Running"]              = 0,
            ["WaitingForActivation"] = 0,
            ["RanToCompletion"]      = 0,
            ["Faulted"]              = 0,
            ["Canceled"]             = 0,
            ["Other"]                = 0,
        };
        var workItems = new Dictionary<string, int>(StringComparer.Ordinal);

        if (ctx.Heap.CanWalkHeap)
        {
            CommandBase.RunStatus("Scanning work items and tasks...", update =>
            {
                long count = 0;
                var sw     = System.Diagnostics.Stopwatch.StartNew();
                foreach (var obj in ctx.Heap.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                    count++;
                    if ((count & 0x3FFF) == 0 && sw.ElapsedMilliseconds >= 200)
                    {
                        update($"Scanning work items and tasks — {count:N0} objects  •  tasks: {taskCounts.Values.Sum()}  work-items: {workItems.Values.Sum()}...");
                        sw.Restart();
                    }
                    var name = obj.Type.Name ?? string.Empty;

                    if (IsTask(name))
                    {
                        string label = GetTaskStateLabel(obj);
                        taskCounts[label] = taskCounts.GetValueOrDefault(label) + 1;
                    }
                    else if (IsWorkItem(name))
                    {
                        workItems[name] = workItems.GetValueOrDefault(name) + 1;
                    }
                }
            });
        }

        return new ThreadPoolData(
            tp.MinThreads,
            tp.MaxThreads,
            tp.ActiveWorkerThreads,
            tp.IdleWorkerThreads,
            true,
            taskCounts,
            workItems);
    }

    private static bool IsTask(string name) =>
        name == "System.Threading.Tasks.Task" ||
        name.StartsWith("System.Threading.Tasks.Task<", StringComparison.Ordinal) ||
        name.StartsWith("System.Threading.Tasks.Task`", StringComparison.Ordinal);

    private static bool IsWorkItem(string name) =>
        name is "System.Threading.QueueUserWorkItemCallback" or
                "System.Threading.QueueUserWorkItemCallbackDefaultContext" ||
        name.Contains("WorkItem",    StringComparison.OrdinalIgnoreCase) ||
        name.Contains("WorkRequest", StringComparison.OrdinalIgnoreCase);

    private string GetTaskStateLabel(Microsoft.Diagnostics.Runtime.ClrObject obj)
    {
        try
        {
            int flags = obj.ReadField<int>("m_stateFlags");
            if ((flags & TASK_STATE_FAULTED) != 0)                return "Faulted";
            if ((flags & TASK_STATE_CANCELED) != 0)               return "Canceled";
            if ((flags & TASK_STATE_RAN_TO_COMPLETION) != 0)      return "RanToCompletion";
            if ((flags & TASK_STATE_DELEGATE_INVOKED) != 0)       return "Running";
            if ((flags & TASK_STATE_STARTED) != 0)                return "WaitingToRun";
            if ((flags & TASK_STATE_WAITING_FOR_ACTIVATION) != 0) return "WaitingForActivation";
        }
        catch { }
        return "Other";
    }
}
