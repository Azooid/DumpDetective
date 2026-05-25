using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using static DumpDetective.Core.Tracing.TraceEventKind;

namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Analyzes Windows kernel CSwitch (context switch) events from a .etl trace.
///
/// CSwitch events fire every time the scheduler moves a CPU from one thread to another.
/// Each event carries the NEW thread (being scheduled in) and the OLD thread (being
/// scheduled away), including why the old thread gave up the CPU.
///
/// Key diagnostics surfaced:
///   • Voluntary vs preempted switch ratio — preempted = CPU-bound / lock-spinning;
///     voluntary = waiting on I/O, locks (acquired after sleep), timers, etc.
///   • Wait-reason distribution — shows what threads are waiting for (I/O, mutexes,
///     thread-pool queue, page faults, etc.)
///   • Per-thread CPU run-slice length — short run slices indicate a thread that keeps
///     getting preempted or wakes up very frequently (can indicate busy-wait / spin)
///   • Top threads by switch count — identifies the most active threads
///
/// Required collection:
///   PerfView:  /KernelEvents:ContextSwitch  (or /KernelEvents:Default)
///   xperf:     -on PROC_THREAD+LOADER+CSWITCH -on MSOS -stackwalk
///
/// NOTE: CSwitch events can be millions per second in a busy system.  The analyzer
/// iterates them in a single pass with O(1) dictionary operations per event.
/// </summary>
public sealed class ContextSwitchTraceAnalyzer
{
    public ContextSwitchTraceData Analyze(string tracePath, int top = 30,
                                           string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return Empty($"Failed: {ex.Message}", processFilter);
        }
    }

    private static readonly Dictionary<string, bool> EvKind = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly Dictionary<int, ThreadAcc> ThreadAccs = new(2048);
        internal readonly Dictionary<int, string> ThreadProcess = new(2048);
        internal readonly Dictionary<int, double> ThreadRunStart = new(2048);
        internal long TotalSwitches;
        internal long VoluntarySwitches;
        internal long PreemptedSwitches;
        internal double FirstEventMs = double.MaxValue;
        internal double LastEventMs;
        internal long IdleSwitches;
        internal double IdleRunMs;
        internal readonly Dictionary<int, double> PerSecond = new(4096);
        internal readonly Dictionary<int, long> WaitReasons = new(64);

        public void Consume(TraceEvent ev, in TraceEventMeta meta, string processName, double timestampMs, int threadId)
        {
            if (!EvKind.TryGetValue(meta.EventName, out bool isCSwitch))
                EvKind[meta.EventName] = isCSwitch =
                    meta.EventName.IndexOf("CSwitch",        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    meta.EventName.IndexOf("Thread/CSwitch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    meta.EventName.IndexOf("Context Switch", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isCSwitch) return;

            double tsMs = timestampMs;
            if (tsMs < FirstEventMs) FirstEventMs = tsMs;
            if (tsMs > LastEventMs)  LastEventMs  = tsMs;

            // ── New thread side ─────────────────────────────────────────
            int newTid = threadId;
            string newProc = processName;

            if (newProc.Length > 0 && !ThreadProcess.ContainsKey(newTid))
                ThreadProcess[newTid] = newProc;

            ThreadRunStart[newTid] = tsMs;

            // ── Old thread side ─────────────────────────────────────────
            int oldTid     = SafeInt(ev, "OldThreadId",       "OldThreadID");
            int oldState   = SafeInt(ev, "OldThreadState");
            int oldReason  = SafeInt(ev, "OldThreadWaitReason");

            string oldProc = ThreadProcess.TryGetValue(oldTid, out var pn) ? pn : "";

            // Apply process filter — include event if either thread matches
            if (_processFilter is not null)
            {
                bool newMatch = newProc.Contains(_processFilter, StringComparison.OrdinalIgnoreCase);
                bool oldMatch = oldProc.Contains(_processFilter, StringComparison.OrdinalIgnoreCase);
                if (!newMatch && !oldMatch) return;
            }

            TotalSwitches++;

            int bucket = (int)(tsMs / 1000.0);
            PerSecond.TryGetValue(bucket, out double prevBucket);
            PerSecond[bucket] = prevBucket + 1;

            bool preempted = (oldState == 1);
            bool voluntary = (oldState == 5);
            if (preempted) PreemptedSwitches++;
            if (voluntary)
            {
                VoluntarySwitches++;
                WaitReasons.TryGetValue(oldReason, out long prev);
                WaitReasons[oldReason] = prev + 1;
            }

            bool isIdle = oldProc.Equals("Idle", StringComparison.OrdinalIgnoreCase) ||
                          (oldProc.Length == 0 && oldTid == 0);

            if (isIdle)
            {
                IdleSwitches++;
                if (ThreadRunStart.TryGetValue(oldTid, out double idleStart))
                    IdleRunMs += tsMs - idleStart;
                return;
            }

            if (!ThreadAccs.TryGetValue(oldTid, out var acc))
                ThreadAccs[oldTid] = acc = new ThreadAcc(oldProc.Length > 0 ? oldProc : newProc);

            if (ThreadRunStart.TryGetValue(oldTid, out double runStart))
                acc.TotalRunMs += tsMs - runStart;

            acc.SwitchCount++;
            if (preempted) acc.Preempted++;
            if (voluntary) acc.Voluntary++;
        }

        public bool WantsEvent(in TraceEventMeta meta)
        {
            if (!EvKind.TryGetValue(meta.EventName, out bool v))
                EvKind[meta.EventName] = v = meta.Kind switch
                {
                    _ when meta.Kind == ThreadContextSwitch => true,
                    _ when meta.IsKnown => false,
                    _ => meta.EventName.IndexOf("CSwitch",        StringComparison.OrdinalIgnoreCase) >= 0 ||
                         meta.EventName.IndexOf("Thread/CSwitch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         meta.EventName.IndexOf("Context Switch", StringComparison.OrdinalIgnoreCase) >= 0
                };
            return v;
        }

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null)
        => new Consumer(processFilter);

    public ContextSwitchTraceData BuildResult(ITraceEventConsumer consumer, string traceFileName,
                                               int top = 30, string? processFilter = null)
    {
        var c = (Consumer)consumer;
        if (c.TotalSwitches == 0)
        {
            string noData = $"{traceFileName}" +
                (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                "  |  No CSwitch events found — collect with /KernelEvents:ContextSwitch";
            return new ContextSwitchTraceData(noData, processFilter,
                0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], null, HasData: false);
        }

        double traceSpanMs = c.LastEventMs > c.FirstEventMs ? c.LastEventMs - c.FirstEventMs : 1;
        double switchesPerSec = c.TotalSwitches * 1000.0 / traceSpanMs;

        // ── Top threads ─────────────────────────────────────────────────────
        var topThreads = c.ThreadAccs
            .OrderByDescending(kv => kv.Value.SwitchCount)
            .Take(top)
            .Select(kv =>
            {
                var a = kv.Value;
                double avgSlice = a.SwitchCount > 0 ? a.TotalRunMs / a.SwitchCount : 0;
                double pct = c.TotalSwitches > 0 ? a.SwitchCount * 100.0 / c.TotalSwitches : 0;
                return new CswitchThreadSummary(
                    a.ProcessName, kv.Key,
                    a.SwitchCount, a.Voluntary, a.Preempted,
                    a.TotalRunMs, avgSlice, pct);
            })
            .ToList();

        // ── Wait reason distribution (voluntary switches only) ───────────────
        long totalWaits = c.WaitReasons.Values.Sum();
        var waitReasonList = c.WaitReasons
            .OrderByDescending(kv => kv.Value)
            .Take(20)
            .Select(kv => new WaitReasonSummary(
                DecodeWaitReason(kv.Key),
                kv.Value,
                totalWaits > 0 ? kv.Value * 100.0 / totalWaits : 0))
            .ToList();

        // ── Timeline ────────────────────────────────────────────────────────
        IReadOnlyList<double>? timeline = null;
        if (c.PerSecond.Count > 1)
        {
            int minB = int.MaxValue, maxB = int.MinValue;
            foreach (int k in c.PerSecond.Keys) { if (k < minB) minB = k; if (k > maxB) maxB = k; }
            var tl = new double[maxB - minB + 1];
            foreach (var kv in c.PerSecond) tl[kv.Key - minB] = kv.Value;
            timeline = tl;
        }

        double voluntaryPct  = c.TotalSwitches > 0 ? c.VoluntarySwitches  * 100.0 / c.TotalSwitches : 0;
        double preemptedPct  = c.TotalSwitches > 0 ? c.PreemptedSwitches  * 100.0 / c.TotalSwitches : 0;

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {c.TotalSwitches:N0} switches  •  {switchesPerSec:F0}/s  •  " +
                      $"{voluntaryPct:F1}% voluntary  •  {preemptedPct:F1}% preempted";

        return new ContextSwitchTraceData(
            info, processFilter,
            c.TotalSwitches, c.VoluntarySwitches, c.PreemptedSwitches,
            switchesPerSec, voluntaryPct, preemptedPct,
            c.IdleSwitches, c.IdleRunMs,
            traceSpanMs, topThreads, waitReasonList, timeline, HasData: true);
    }

    public ContextSwitchTraceData Analyze(TraceLog trace, string traceFileName,
                                           int top = 30, string? processFilter = null,
                                           Action<string>? progress = null)
    {
        try
        {
            var c = CreateConsumer(processFilter);
            TraceEventDispatcher.Dispatch(trace, c,
                progress);
            return BuildResult(c, traceFileName, top, processFilter);
        }
        catch (Exception ex)
        {
            return Empty($"Failed during event pass: {ex.Message}", processFilter);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Wait-reason decoding (Windows KWAIT_REASON enum)
    // ─────────────────────────────────────────────────────────────────────────
    private static string DecodeWaitReason(int r) => r switch
    {
        0  => "Executive",
        1  => "FreePage",
        2  => "PageIn (Page Fault)",
        3  => "PoolAllocation",
        4  => "DelayExecution (Sleep / Timer)",
        5  => "Suspended",
        6  => "UserRequest",
        7  => "WrExecutive",
        8  => "WrFreePage",
        9  => "WrPageIn (Hard Page Fault)",
        10 => "WrPoolAllocation",
        11 => "WrDelayExecution (Timer / Sleep)",
        12 => "WrSuspended",
        13 => "WrUserRequest",
        14 => "WrEventPair",
        15 => "WrQueue (ThreadPool / I/O Completion)",
        16 => "WrLpcReceive (COM / RPC)",
        17 => "WrLpcReply (COM / RPC)",
        18 => "WrVirtualMemory",
        19 => "WrPageOut",
        20 => "WrRendezvous",
        21 => "WrKeyedEvent",
        22 => "WrTerminated",
        23 => "WrProcessInSwap",
        24 => "WrCpuRateControl",
        25 => "WrCalloutStack",
        26 => "WrKernel",
        27 => "WrResource (Critical Section / Mutex)",
        28 => "WrPushLock",
        29 => "WrMutex",
        30 => "WrQuantumEnd (Preempted)",
        31 => "WrDispatchInt",
        32 => "WrPreempted",
        33 => "WrYieldExecution",
        34 => "WrFastMutex",
        35 => "WrGuardedMutex",
        36 => "WrRundown",
        _  => $"Unknown ({r})",
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static ContextSwitchTraceData Empty(string info, string? filter) =>
        new(info, filter, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], null, HasData: false);

    // ─────────────────────────────────────────────────────────────────────────
    // Mutable accumulator (local to analyzer)
    // ─────────────────────────────────────────────────────────────────────────
    private sealed class ThreadAcc(string processName)
    {
        public readonly string ProcessName = processName;
        public long   SwitchCount;
        public long   Voluntary;
        public long   Preempted;
        public double TotalRunMs;
    }
}
