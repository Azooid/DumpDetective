using DumpDetective.Core.Tracing;
using static DumpDetective.Core.Tracing.TraceEventKind;
using static DumpDetective.Analysis.Trace.BuiltInClassifiers.ClassifierMatch;

namespace DumpDetective.Analysis.Trace.BuiltInClassifiers;

/// <summary>
/// Classifies thread, ThreadPool, Task, and TPL events from:
///   Microsoft-Windows-DotNETRuntime (ThreadPool/*, Thread/CSwitch|Creating|Running)
///   System.Threading.Tasks.TplEventSource (TaskExecute|TaskScheduled|TaskWait|TraceSynchronousWork)
///   System.Diagnostics.Eventing.FrameworkEventSource (ThreadPoolEnqueueWork|DequeueWork)
///   Windows Kernel (Thread/CSwitch)
/// </summary>
internal sealed class ThreadingClassifier : IProviderScopedClassifier
{
    public IReadOnlyList<string> Providers { get; } =
    [
        "Microsoft-Windows-DotNETRuntime",
        "System.Threading.Tasks.TplEventSource",
        "System.Diagnostics.Eventing.FrameworkEventSource",
        "Windows Kernel",
    ];

    public TraceEventKind Classify(string provider, string name)
    {
        // ── Task lifecycle ────────────────────────────────────────────────────
        if (Contains(name, "Task/Scheduled")   || Contains(name, "TaskScheduled"))    return TaskScheduled;
        if (Contains(name, "Task/Execute/Start") || Contains(name, "TaskStarted"))    return TaskStarted;
        if (Contains(name, "Task/Execute/Stop")  || Contains(name, "TaskCompleted")  ||
            Contains(name, "Task/Completed"))                                          return TaskCompleted;
        if (Contains(name, "Task/Wait/Begin")  || Contains(name, "TaskWaitBegin"))    return TaskWaitBegin;
        if (Contains(name, "Task/Wait/End")    || Contains(name, "TaskWaitEnd"))      return TaskWaitEnd;

        // ── TPL internals (TplEventSource) ────────────────────────────────────
        if (Contains(name, "TaskExecute/Start")    || Contains(name, "TaskExecuteStart"))   return TaskExecuteStart;
        if (Contains(name, "TaskExecute/Stop")     || Contains(name, "TaskExecuteStop"))    return TaskExecuteStop;
        if (Contains(name, "TaskScheduled/Send")   || Contains(name, "TaskScheduledSend"))  return TaskScheduledSend;
        if (Contains(name, "TaskWait/Send")        || Contains(name, "TaskWaitSend"))       return TaskWaitSend;
        if (Contains(name, "TraceSynchronousWork/Start"))                                    return TraceSynchronousWorkStart;
        if (Contains(name, "TraceSynchronousWork/Stop"))                                     return TraceSynchronousWorkStop;

        // ── Awaiter / continuation ────────────────────────────────────────────
        if (Contains(name, "Awaiter") || Contains(name, "ContinuationScheduled") ||
            Contains(name, "ScheduleContinuation"))
            return AwaiterContinuation;

        // ── ThreadPool work items ─────────────────────────────────────────────
        if (Contains(name, "ThreadPool/Enqueue")   || Contains(name, "ThreadPoolEnqueueWork")) return ThreadPoolEnqueue;
        if (Contains(name, "ThreadPool/Dequeue")   || Contains(name, "ThreadPoolDequeueWork")) return ThreadPoolDequeue;

        // ── Worker thread lifecycle ───────────────────────────────────────────
        if (Contains(name, "ThreadPoolWorkerThread/Start") || EndsWith(name, "WorkerThreadStart")) return ThreadPoolWorkerStart;
        if (Contains(name, "ThreadPoolWorkerThread/Stop")  || EndsWith(name, "WorkerThreadStop"))  return ThreadPoolWorkerStop;
        if (Contains(name, "ThreadPoolWorkerThread/Wait")  || EndsWith(name, "WorkerThreadWait"))  return ThreadPoolWorkerWait;

        // ── ThreadPool hill-climbing adjustment ───────────────────────────────
        if (Contains(name, "Adjustment/Sample") || Contains(name, "AdjustmentSample")) return ThreadPoolAdjustmentSample;
        if (Contains(name, "Adjustment/Stats")  || Contains(name, "AdjustmentStats"))  return ThreadPoolAdjustmentStats;
        if (Contains(name, "ThreadPoolWorkerThreadAdjustment") ||
            (Contains(name, "Adjustment") && Contains(name, "ThreadPool")))
            return ThreadPoolAdjustment;

        // ── Context switch + thread lifecycle ─────────────────────────────────
        if (Contains(name, "Thread/CSwitch")  || Contains(name, "CSwitch"))             return ThreadContextSwitch;
        if (Contains(name, "Thread/Sample")   || Contains(name, "ThreadSample"))        return CpuSample;
        if (Contains(name, "Thread/Creating") || Contains(name, "ThreadCreating"))      return ThreadCreating;
        if (Contains(name, "Thread/Running")  || Contains(name, "ThreadRunning"))       return ThreadRunning;

        return Unknown;
    }
}
