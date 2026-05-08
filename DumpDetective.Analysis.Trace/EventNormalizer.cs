using DumpDetective.Core.Tracing;
using Microsoft.Diagnostics.Tracing;

namespace DumpDetective.Analysis.Trace;

/// <summary>
/// Centralises the fragile <c>EventName.Contains(...)</c> matching that is otherwise
/// scattered across every analyzer.  Call <see cref="Classify"/> once per event and
/// switch on the returned <see cref="TraceEventKind"/> instead.
///
/// Patterns are derived from real ETW captures:
///   Windows Kernel/PerfInfo/Sample
///   Microsoft-Windows-DotNETRuntime/GC/Start|Stop|SuspendEEStart|SuspendEEStop|…
///   Microsoft-Windows-DotNETRuntime/Contention/Start|Stop
///   Microsoft-Windows-DotNETRuntime/Exception/Start · ExceptionCatch/Start|Stop
///   Microsoft-Windows-DotNETRuntime/Method/JittingStarted|LoadVerbose|InliningSucceeded|…
///   Microsoft-Windows-DotNETRuntime/ThreadPool/Enqueue|Dequeue
///   Microsoft-Windows-ASPNET/Request/Start|Stop
///   AspNetTrace/AspNetReq/Start|Stop
///   Microsoft-AdoNet-SystemData/BeginExecute|EndExecute
///   System.Diagnostics.Eventing.FrameworkEventSource/GetResponse/Start|Stop
///   System.Diagnostics.Eventing.FrameworkEventSource/GetRequestStream/Start|Stop
///   System.Diagnostics.Eventing.FrameworkEventSource/ThreadPoolEnqueueWork|DequeueWork
///   Windows Kernel/Process/Start|Stop · Windows Kernel/Thread/CSwitch
///
/// Design: first-char routing reduces the per-event branch count to ~3 on average.
/// All comparisons are OrdinalIgnoreCase.
/// </summary>
public static class EventNormalizer
{
    /// <summary>
    /// Classifies a raw trace event into a <see cref="TraceEventKind"/>.
    /// Returns <see cref="TraceEventKind.Unknown"/> for events this tool does not handle.
    /// </summary>
    public static TraceEventKind Classify(TraceEvent ev)
    {
        string name = ev.EventName ?? "";
        if (name.Length == 0) return TraceEventKind.Unknown;

        // Route by first character — OR with 0x20 lowercases ASCII letters cheaply.
        char first = (char)(name[0] | 0x20);
        return first switch
        {
            'a' => ClassifyA(name),
            'c' => ClassifyC(name),
            'e' => ClassifyE(name),
            'g' => ClassifyG(name),
            'h' => ClassifyH(name),
            'j' => ClassifyJ(name),
            'm' => ClassifyM(name),
            'p' => ClassifyP(name),
            's' => ClassifyS(name),
            't' => ClassifyT(name),
            'w' => ClassifyW(name),
            _   => TraceEventKind.Unknown
        };
    }

    // ── A ────────────────────────────────────────────────────────────────────
    // AspNetTrace/AspNetReq/Start|Stop
    // GC/AllocationTick  (via "AllocationTick")
    // AwaiterContinuation
    // ThreadPoolWorkerThreadAdjustment/Adjustment
    private static TraceEventKind ClassifyA(string n)
    {
        // AspNetTrace/AspNetReq/Start  →  HttpRequestStart
        if (Contains(n, "AspNetReq") || Contains(n, "AspNetTrace"))
        {
            if (EndsWith(n, "/Start") || EndsWith(n, "Start")) return TraceEventKind.HttpRequestStart;
            if (EndsWith(n, "/Stop")  || EndsWith(n, "Stop"))  return TraceEventKind.HttpRequestStop;
        }
        if (Contains(n, "AllocationTick")) return TraceEventKind.GCAllocationTick;
        if (Contains(n, "Adjustment"))     return TraceEventKind.ThreadPoolAdjustment;
        if (Contains(n, "Awaiter") || Contains(n, "ContinuationScheduled") || Contains(n, "ScheduleContinuation"))
            return TraceEventKind.AwaiterContinuation;
        return TraceEventKind.Unknown;
    }

    // ── C ────────────────────────────────────────────────────────────────────
    // Microsoft-Windows-DotNETRuntime/Contention/Start|Stop
    // ExceptionCatch/Start|Stop  (also starts with 'e' but routed here when prefixed by something)
    private static TraceEventKind ClassifyC(string n)
    {
        if (Contains(n, "Contention/Start") || EndsWith(n, "ContentionStart"))
            return TraceEventKind.ContentionStart;
        if (Contains(n, "Contention/Stop") || EndsWith(n, "ContentionStop"))
            return TraceEventKind.ContentionStop;
        if (Contains(n, "ContinuationScheduled")) return TraceEventKind.AwaiterContinuation;
        if (Contains(n, "CSwitch"))               return TraceEventKind.ThreadContextSwitch;
        return TraceEventKind.Unknown;
    }

    // ── E ────────────────────────────────────────────────────────────────────
    // Microsoft-Windows-DotNETRuntime/Exception/Start
    // Microsoft-Windows-DotNETRuntime/ExceptionCatch/Start|Stop
    private static TraceEventKind ClassifyE(string n)
    {
        if (Contains(n, "ExceptionCatch/Stop")  || EndsWith(n, "ExceptionCatchStop"))
            return TraceEventKind.ExceptionCatchStop;
        if (Contains(n, "ExceptionCatch/Start") || EndsWith(n, "ExceptionCatchStart"))
            return TraceEventKind.ExceptionCatchStart;
        // Exception/Start is the first-chance throw; Exception/Stop is rarely emitted
        if (Contains(n, "Exception/Start") || EndsWith(n, "ExceptionThrown") || EndsWith(n, "Exception"))
            return TraceEventKind.ExceptionThrown;
        return TraceEventKind.Unknown;
    }

    // ── G ────────────────────────────────────────────────────────────────────
    // Microsoft-Windows-DotNETRuntime/GC/Start|Stop|SuspendEEStart|SuspendEEStop|
    //   RestartEEStart|RestartEEStop|HeapStats|AllocationTick|FinalizeObject
    private static TraceEventKind ClassifyG(string n)
    {
        // Order matters: more-specific checks before generic GCStart/Stop
        if (Contains(n, "SuspendEEStop")  || Contains(n, "GC/SuspendEEStop"))  return TraceEventKind.GCSuspendEEStop;
        if (Contains(n, "SuspendEEStart") || Contains(n, "GC/SuspendEEStart")) return TraceEventKind.GCSuspendEEStart;
        if (Contains(n, "RestartEEStop")  || Contains(n, "GC/RestartEEStop"))  return TraceEventKind.GCRestartEEStop;
        if (Contains(n, "RestartEEStart") || Contains(n, "GC/RestartEEStart")) return TraceEventKind.GCRestartEEStart;
        if (Contains(n, "FinalizeObject"))  return TraceEventKind.GCFinalizeObject;
        if (Contains(n, "GCHeapStats")   || Contains(n, "GC/HeapStats"))       return TraceEventKind.GCHeapStats;
        if (Contains(n, "AllocationTick") || Contains(n, "GC/AllocationTick")) return TraceEventKind.GCAllocationTick;
        if (Contains(n, "GC/Start")  || EndsWith(n, "GCStart"))  return TraceEventKind.GCStart;
        if (Contains(n, "GC/Stop")   || EndsWith(n, "GCStop"))   return TraceEventKind.GCStop;
        // GetResponse / GetRequestStream from FrameworkEventSource routed by 'g'? No — they start with 'S'.
        return TraceEventKind.Unknown;
    }

    // ── H ────────────────────────────────────────────────────────────────────
    // Microsoft-Windows-ASPNET/Request/Start|Stop  (provider starts with "Microsoft", but
    //   EventName returned by TraceLog is "Request/Start" — first char 'r', not 'h').
    //   When the full provider+event string appears as EventName, first char may be 'h'.
    // AspNetTrace/AspNetReq/Start|Stop  → handled in 'a'
    // Microsoft-AspNetCore-Hosting/*  → handled in 'm'
    private static TraceEventKind ClassifyH(string n)
    {
        // Bare "Request/Start" event name (provider already stripped)
        if (Contains(n, "Request/Start") || Contains(n, "RequestStart") || Contains(n, "BeginRequest"))
            return TraceEventKind.HttpRequestStart;
        if (Contains(n, "Request/Stop") || Contains(n, "RequestStop") || Contains(n, "EndRequest"))
            return TraceEventKind.HttpRequestStop;
        return TraceEventKind.Unknown;
    }

    // ── J ────────────────────────────────────────────────────────────────────
    // Microsoft-Windows-DotNETRuntime/Method/JittingStarted|LoadVerbose|
    //   InliningSucceeded|InliningFailedAnsi
    private static TraceEventKind ClassifyJ(string n)
    {
        if (Contains(n, "JittingStarted") || Contains(n, "Method/JittingStarted"))
            return TraceEventKind.JitMethodStart;
        if (Contains(n, "LoadVerbose")    || Contains(n, "Method/LoadVerbose"))
            return TraceEventKind.JitMethodLoad;
        if (Contains(n, "InliningSucceeded"))   return TraceEventKind.JitInliningSucceeded;
        if (Contains(n, "InliningFailed"))       return TraceEventKind.JitInliningFailed;
        return TraceEventKind.Unknown;
    }

    // ── M ────────────────────────────────────────────────────────────────────
    // Microsoft-Windows-ASPNET/Request/Start|Stop
    // Microsoft-AspNetCore-Hosting/Request/Start|Stop
    // Microsoft-AdoNet-SystemData/BeginExecute|EndExecute
    // Microsoft-Windows-DotNETRuntime/Method/*  (duplicate of 'j' in case full name is returned)
    // Microsoft-Windows-DotNETRuntime/ThreadPool/Enqueue|Dequeue
    private static TraceEventKind ClassifyM(string n)
    {
        // ADO.NET system data — must check before generic Request matching
        if (Contains(n, "AdoNet") || Contains(n, "SystemData") || Contains(n, "Ado-Net"))
        {
            if (Contains(n, "BeginExecute")) return TraceEventKind.SqlCommandStart;
            if (Contains(n, "EndExecute"))   return TraceEventKind.SqlCommandStop;
        }

        // HTTP via ASPNET or AspNetCore providers
        if (Contains(n, "ASPNET") || Contains(n, "AspNetCore"))
        {
            if (Contains(n, "Request/Start") || Contains(n, "RequestStart")) return TraceEventKind.HttpRequestStart;
            if (Contains(n, "Request/Stop")  || Contains(n, "RequestStop"))  return TraceEventKind.HttpRequestStop;
        }

        // JIT method events (full provider-qualified name)
        if (Contains(n, "JittingStarted"))     return TraceEventKind.JitMethodStart;
        if (Contains(n, "LoadVerbose"))        return TraceEventKind.JitMethodLoad;
        if (Contains(n, "InliningSucceeded"))  return TraceEventKind.JitInliningSucceeded;
        if (Contains(n, "InliningFailed"))     return TraceEventKind.JitInliningFailed;

        // ThreadPool enqueue/dequeue (provider-qualified)
        if (Contains(n, "ThreadPool/Enqueue")) return TraceEventKind.ThreadPoolEnqueue;
        if (Contains(n, "ThreadPool/Dequeue")) return TraceEventKind.ThreadPoolDequeue;

        return TraceEventKind.Unknown;
    }

    // ── P ────────────────────────────────────────────────────────────────────
    // Windows Kernel/PerfInfo/Sample  →  CpuSample
    // Windows Kernel/Process/Start|Stop
    // Microsoft-Windows-Kernel-Process/ProcessStart/Start|ProcessStop/Stop
    private static TraceEventKind ClassifyP(string n)
    {
        if (Contains(n, "PerfInfo") || Contains(n, "SampledProfile"))
            return TraceEventKind.CpuSample;
        if (Contains(n, "ProcessStart") || (Contains(n, "Process/Start") && !Contains(n, "GetRequest")))
            return TraceEventKind.ProcessStart;
        if (Contains(n, "ProcessStop")  || Contains(n, "Process/Stop"))
            return TraceEventKind.ProcessStop;
        return TraceEventKind.Unknown;
    }

    // ── R ────────────────────────────────────────────────────────────────────
    // Microsoft-Windows-ASPNET/Request/Start|Stop  when EventName = "Request/Start"
    // (TraceLog sometimes returns bare task/opcode name without provider prefix)
    // This branch is reachable via the fallthrough '_' — add explicit 'r' routing above if needed.

    // ── S ────────────────────────────────────────────────────────────────────
    // Windows Kernel/PerfInfo/Sample  (already caught in 'p', but TraceLog sometimes
    //   surfaces the event name as "SampledProfile" alone)
    // System.Diagnostics.Eventing.FrameworkEventSource/GetRequestStream/Start|Stop
    // System.Diagnostics.Eventing.FrameworkEventSource/GetResponse/Start|Stop
    // System.Diagnostics.Eventing.FrameworkEventSource/ThreadPoolEnqueueWork|DequeueWork
    // System.Data.SqlClient.EventSource/SqlCommand/…/Start|Stop
    private static TraceEventKind ClassifyS(string n)
    {
        if (Contains(n, "SampledProfile") || Contains(n, "cpu-sampling"))
            return TraceEventKind.CpuSample;

        // FrameworkEventSource — HttpClient-level HTTP events
        if (Contains(n, "GetRequestStream"))
        {
            if (Contains(n, "Start")) return TraceEventKind.HttpClientGetRequestStart;
            if (Contains(n, "Stop"))  return TraceEventKind.HttpClientGetRequestStop;
        }
        if (Contains(n, "GetResponse") && !Contains(n, "GetResponseHeader"))
        {
            if (Contains(n, "Start")) return TraceEventKind.HttpClientGetResponseStart;
            if (Contains(n, "Stop"))  return TraceEventKind.HttpClientGetResponseStop;
        }

        // FrameworkEventSource thread-pool
        if (Contains(n, "ThreadPoolEnqueueWork")) return TraceEventKind.ThreadPoolEnqueue;
        if (Contains(n, "ThreadPoolDequeueWork")) return TraceEventKind.ThreadPoolDequeue;

        // System.Data.SqlClient / ADO.NET  (legacy .NET Framework)
        if (Contains(n, "SqlCommand") || Contains(n, "CommandExecut"))
        {
            if (Contains(n, "Start") || Contains(n, "Begin") || Contains(n, "Executing"))
                return TraceEventKind.SqlCommandStart;
            if (Contains(n, "Stop") || Contains(n, "End") || Contains(n, "Executed") || Contains(n, "Error"))
                return TraceEventKind.SqlCommandStop;
        }
        if (Contains(n, "SqlConnection"))
        {
            if (Contains(n, "Open"))  return TraceEventKind.SqlConnectionOpen;
            if (Contains(n, "Close")) return TraceEventKind.SqlConnectionClose;
        }

        if (Contains(n, "ScheduleContinuation")) return TraceEventKind.AwaiterContinuation;
        return TraceEventKind.Unknown;
    }

    // ── T ────────────────────────────────────────────────────────────────────
    // Task Scheduled / Started / Completed / WaitBegin / WaitEnd
    // ThreadPool/Enqueue|Dequeue · ThreadPoolWorkerThreadAdjustment
    // Windows Kernel/Thread/CSwitch
    private static TraceEventKind ClassifyT(string n)
    {
        if (Contains(n, "Task/Scheduled") || Contains(n, "TaskScheduled"))    return TraceEventKind.TaskScheduled;
        if (Contains(n, "Task/Execute/Start") || Contains(n, "TaskStarted"))  return TraceEventKind.TaskStarted;
        if (Contains(n, "Task/Execute/Stop")  || Contains(n, "TaskCompleted") || Contains(n, "Task/Completed"))
            return TraceEventKind.TaskCompleted;
        if (Contains(n, "Task/Wait/Begin") || Contains(n, "TaskWaitBegin"))   return TraceEventKind.TaskWaitBegin;
        if (Contains(n, "Task/Wait/End")   || Contains(n, "TaskWaitEnd"))     return TraceEventKind.TaskWaitEnd;

        if (Contains(n, "ThreadPool/Enqueue")) return TraceEventKind.ThreadPoolEnqueue;
        if (Contains(n, "ThreadPool/Dequeue")) return TraceEventKind.ThreadPoolDequeue;
        if (Contains(n, "ThreadPoolWorkerThreadAdjustment") || Contains(n, "Adjustment"))
            return TraceEventKind.ThreadPoolAdjustment;

        if (Contains(n, "Thread/CSwitch") || Contains(n, "CSwitch"))          return TraceEventKind.ThreadContextSwitch;
        return TraceEventKind.Unknown;
    }

    // ── W ────────────────────────────────────────────────────────────────────
    // WaitHandle/Wait/Start|Stop  (WaitHandleWaitStart|Stop)
    // Windows Kernel/PerfInfo/Sample  — already caught in 'p'
    private static TraceEventKind ClassifyW(string n)
    {
        if (Contains(n, "WaitHandleWaitStart") || (Contains(n, "WaitHandleWait") && Contains(n, "Start")))
            return TraceEventKind.WaitHandleWaitStart;
        if (Contains(n, "WaitHandleWaitStop")  || (Contains(n, "WaitHandleWait") && Contains(n, "Stop")))
            return TraceEventKind.WaitHandleWaitStop;
        return TraceEventKind.Unknown;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────
    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool EndsWith(string s, string suffix) =>
        s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
}