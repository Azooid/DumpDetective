using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis.Analyzers;

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
            CommandBase.RunStatus("Scanning work items and tasks...", () =>
            {
                foreach (var obj in ctx.Heap.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
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
