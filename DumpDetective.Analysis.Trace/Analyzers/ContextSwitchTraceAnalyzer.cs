using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

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

    public ContextSwitchTraceData Analyze(TraceLog trace, string traceFileName,
                                           int top = 30, string? processFilter = null,
                                           Action<string>? progress = null)
    {
        // ── Per-thread accumulators ─────────────────────────────────────────
        // Keyed by OLD thread ID (the thread being switched away from)
        var threadAcc = new Dictionary<int, ThreadAcc>(2048);

        // thread → process name map built from "new thread" side of each CSwitch
        var threadProcess = new Dictionary<int, string>(2048);

        // thread → last run-start timestamp (when it was last scheduled IN)
        var threadRunStart = new Dictionary<int, double>(2048);

        // ── Aggregate counters ─────────────────────────────────────────────
        long   totalSwitches    = 0;
        long   voluntarySwitches = 0;
        long   preemptedSwitches = 0;
        double firstEventMs      = double.MaxValue;
        double lastEventMs       = 0;

        // Idle thread tracking (Windows Idle process, PID 0 per-CPU idle threads)
        long   idleSwitches = 0;
        double idleRunMs    = 0;

        // Per-second bucket for timeline sparkline
        var perSecond = new Dictionary<int, double>(4096);

        // Wait reason aggregate (OldThreadState=5 only)
        var waitReasons = new Dictionary<int, long>(64);

        long evTotal = trace.EventCount;
        long evProcessed = 0;
        long lastProgressMs = 0;
        var evKind = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var ev in trace.Events)
            {
                evProcessed++;
                if (progress is not null && Environment.TickCount64 - lastProgressMs >= 200)
                {
                    progress($"{totalSwitches:N0} ctx switches");
                    lastProgressMs = Environment.TickCount64;
                }
                // Only process CSwitch events
                string evName = ev.EventName ?? "";
                if (!evKind.TryGetValue(evName, out bool isCSwitch))
                    evKind[evName] = isCSwitch =
                        evName.IndexOf("CSwitch",        StringComparison.OrdinalIgnoreCase) >= 0 ||
                        evName.IndexOf("Thread/CSwitch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        evName.IndexOf("Context Switch", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isCSwitch) continue;

                double tsMs = ev.TimeStampRelativeMSec;
                if (tsMs < firstEventMs) firstEventMs = tsMs;
                if (tsMs > lastEventMs)  lastEventMs  = tsMs;

                // ── New thread side ─────────────────────────────────────────
                int newTid = ev.ThreadID;
                string newProc = ev.ProcessName ?? "";

                // Register the new thread's process name (for resolving old thread later)
                if (newProc.Length > 0 && !threadProcess.ContainsKey(newTid))
                    threadProcess[newTid] = newProc;

                // Record when this thread started its current run slice
                threadRunStart[newTid] = tsMs;

                // ── Old thread side ─────────────────────────────────────────
                int oldTid     = SafeInt(ev, "OldThreadId",       "OldThreadID");
                int oldState   = SafeInt(ev, "OldThreadState");
                int oldReason  = SafeInt(ev, "OldThreadWaitReason");

                // Resolve old thread's process name (may be unknown at start of trace)
                string oldProc = threadProcess.TryGetValue(oldTid, out var pn) ? pn : "";

                // Apply process filter — include event if either thread matches
                if (processFilter is not null)
                {
                    bool newMatch = newProc.Contains(processFilter, StringComparison.OrdinalIgnoreCase);
                    bool oldMatch = oldProc.Contains(processFilter, StringComparison.OrdinalIgnoreCase);
                    if (!newMatch && !oldMatch) continue;
                }

                totalSwitches++;

                // Per-second bucket
                int bucket = (int)(tsMs / 1000.0);
                perSecond.TryGetValue(bucket, out double prevBucket);
                perSecond[bucket] = prevBucket + 1;

                // Classify old thread's exit reason.
                // OldThreadState is the state the old thread TRANSITIONS INTO:
                //   1 = Ready   → thread is still runnable but was preempted (quantum expiry
                //                 or a higher-priority thread became ready)
                //   5 = Waiting → thread voluntarily blocked (I/O, mutex, timer, etc.)
                bool preempted = (oldState == 1);  // Ready → preempted
                bool voluntary = (oldState == 5);  // Waiting → blocked voluntarily
                if (preempted) preemptedSwitches++;
                if (voluntary)
                {
                    voluntarySwitches++;
                    waitReasons.TryGetValue(oldReason, out long prev);
                    waitReasons[oldReason] = prev + 1;
                }

                // ── Per-thread accumulation for old thread ──────────────────
                // Idle process threads (process name "Idle", or TID 0 with no process name)
                // represent CPU time when no real thread was runnable. Accumulate separately
                // so they do not pollute the top-threads table.
                bool isIdle = oldProc.Equals("Idle", StringComparison.OrdinalIgnoreCase) ||
                              (oldProc.Length == 0 && oldTid == 0);

                if (isIdle)
                {
                    idleSwitches++;
                    if (threadRunStart.TryGetValue(oldTid, out double idleStart))
                        idleRunMs += tsMs - idleStart;
                    continue;
                }

                if (!threadAcc.TryGetValue(oldTid, out var acc))
                    threadAcc[oldTid] = acc = new ThreadAcc(oldProc.Length > 0 ? oldProc : newProc);

                // If we have a run-start timestamp for this thread, compute run duration
                if (threadRunStart.TryGetValue(oldTid, out double runStart))
                    acc.TotalRunMs += tsMs - runStart;

                acc.SwitchCount++;
                if (preempted) acc.Preempted++;
                if (voluntary) acc.Voluntary++;
            }
        }
        catch (Exception ex)
        {
            return Empty($"Failed during event pass: {ex.Message}", processFilter);
        }

        if (totalSwitches == 0)
        {
            string noData = $"{traceFileName}" +
                (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                "  |  No CSwitch events found — collect with /KernelEvents:ContextSwitch";
            return new ContextSwitchTraceData(noData, processFilter,
                0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], null, HasData: false);
        }

        double traceSpanMs = lastEventMs > firstEventMs ? lastEventMs - firstEventMs : 1;
        double switchesPerSec = totalSwitches * 1000.0 / traceSpanMs;

        // ── Top threads ─────────────────────────────────────────────────────
        var topThreads = threadAcc
            .OrderByDescending(kv => kv.Value.SwitchCount)
            .Take(top)
            .Select(kv =>
            {
                var a = kv.Value;
                double avgSlice = a.SwitchCount > 0 ? a.TotalRunMs / a.SwitchCount : 0;
                double pct = totalSwitches > 0 ? a.SwitchCount * 100.0 / totalSwitches : 0;
                return new CswitchThreadSummary(
                    a.ProcessName, kv.Key,
                    a.SwitchCount, a.Voluntary, a.Preempted,
                    a.TotalRunMs, avgSlice, pct);
            })
            .ToList();

        // ── Wait reason distribution (voluntary switches only) ───────────────
        long totalWaits = waitReasons.Values.Sum();
        var waitReasonList = waitReasons
            .OrderByDescending(kv => kv.Value)
            .Take(20)
            .Select(kv => new WaitReasonSummary(
                DecodeWaitReason(kv.Key),
                kv.Value,
                totalWaits > 0 ? kv.Value * 100.0 / totalWaits : 0))
            .ToList();

        // ── Timeline ────────────────────────────────────────────────────────
        IReadOnlyList<double>? timeline = null;
        if (perSecond.Count > 1)
        {
            int minB = int.MaxValue, maxB = int.MinValue;
            foreach (int k in perSecond.Keys) { if (k < minB) minB = k; if (k > maxB) maxB = k; }
            var tl = new double[maxB - minB + 1];
            foreach (var kv in perSecond) tl[kv.Key - minB] = kv.Value;
            timeline = tl;
        }

        double voluntaryPct  = totalSwitches > 0 ? voluntarySwitches  * 100.0 / totalSwitches : 0;
        double preemptedPct  = totalSwitches > 0 ? preemptedSwitches  * 100.0 / totalSwitches : 0;

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {totalSwitches:N0} switches  •  {switchesPerSec:F0}/s  •  " +
                      $"{voluntaryPct:F1}% voluntary  •  {preemptedPct:F1}% preempted";

        return new ContextSwitchTraceData(
            info, processFilter,
            totalSwitches, voluntarySwitches, preemptedSwitches,
            switchesPerSec, voluntaryPct, preemptedPct,
            idleSwitches, idleRunMs,
            traceSpanMs, topThreads, waitReasonList, timeline, HasData: true);
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

    private static int SafeInt(TraceEvent ev, params string[] fields)
    {
        foreach (var field in fields)
        {
            try
            {
                var v = ev.PayloadByName(field);
                if (v is not null)
                    return (int)Convert.ChangeType(v, typeof(int));
            }
            catch { }
        }
        return 0;
    }

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
