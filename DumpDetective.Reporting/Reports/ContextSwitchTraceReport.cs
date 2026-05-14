using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class ContextSwitchTraceReport
{
    public void Render(ContextSwitchTraceData data, IRenderSink sink, int top = 30)
    {
        sink.Explain(
            what: "Context-switch analysis — measures thread scheduling activity from Windows kernel CSwitch events. " +
                  "Every time the OS scheduler moves a CPU from one thread to another, a CSwitch event fires. " +
                  "The event records whether the old thread was forced off (preempted) or chose to block (voluntary), " +
                  "and if voluntary, exactly what it was waiting for (I/O, lock, timer, thread-pool queue, etc.).",
            why:  "Context switches are the scheduler's heartbeat. The mix of voluntary vs preempted, the rate per second, " +
                  "and the average CPU run-slice length together tell you whether the process is CPU-bound, I/O-bound, " +
                  "lock-contended, or just normally busy. Each pattern points to a different fix.",
            impact: "This report is the earliest warning of thread-pool starvation, lock hotspots, and working-set pressure " +
                    "— often visible here before latency metrics degrade.",
            bullets: [
                // ── What the numbers mean ──────────────────────────────────────
                "TERM: Voluntary switch — thread willingly gave up the CPU: blocked on I/O, acquired a lock after waiting, " +
                    "called Sleep, or returned to the thread-pool queue. Normal and expected.",
                "TERM: Preempted switch — OS forced the thread off the CPU because its time quantum expired or a higher-priority " +
                    "thread became ready. Indicates CPU-bound work.",
                "TERM: Avg run slice — mean CPU burst duration per switch. Short slices = thread wakes up briefly then blocks " +
                    "again (spinning / chatty I/O). Long slices = thread does sustained CPU work.",
                "TERM: CPU idle % — fraction of wall-clock time no real thread was runnable. High idle = CPU headroom available.",
                "TERM: Wait reason — kernel enum describing WHY a thread voluntarily blocked " +
                    "(e.g. WrQueue = thread-pool idle, WrResource = mutex, WrLpcReceive = COM/RPC, DelayExecution = Sleep/Timer).",
                // ── Good / Bad benchmarks ──────────────────────────────────────
                "GOOD: Voluntary % > 70% — most switches are thread-pool or I/O waits; CPU is not saturated.",
                "GOOD: Preempted % < 20% — low preemption means CPU demand is within capacity.",
                "GOOD: Avg run slice 5–50 ms — threads do meaningful bursts of work before blocking; no spinning.",
                "GOOD: CPU idle > 30% — healthy headroom; system can absorb traffic spikes.",
                "GOOD: Dominant wait reason = WrQueue — thread-pool threads spend most time waiting for work items; healthy idle pattern.",
                "BAD:  Preempted % > 40% — CPU saturation; threads are being forcibly evicted. Scale out or reduce CPU work.",
                "BAD:  Preempted % 20–40% — elevated CPU pressure; profile with cpu-trace to find hot methods.",
                "BAD:  Avg run slice < 1 ms with high switch count — spinning or busy-wait loop; check contention-trace.",
                "BAD:  Dominant wait reason = WrResource or WrMutex > 30% — lock contention bottleneck; run contention-trace.",
                "BAD:  Dominant wait reason = PageIn > 10% — hard page faults; working set exceeds available RAM.",
                "BAD:  Dominant wait reason = WrLpcReceive > 20% — COM/RPC overhead; reduce inter-process calls.",
                "BAD:  CPU idle < 10% — system is near fully saturated across all cores.",
            ],
            action: "Step 1 — Check preempted %: >40% = CPU saturation (scale out / optimize hot methods via cpu-trace). " +
                    "Step 2 — Check dominant wait reason: WrResource = lock contention (contention-trace); " +
                    "WrQueue = normal idle (run thread-pool-starvation to confirm); PageIn = memory pressure. " +
                    "Step 3 — Check avg run slice in top-threads table: <1 ms on a busy thread = spinning or timer storm."
        );

        sink.Section("Context Switch Summary", "cswitch-summary");
        sink.KeyValues([
            ("Trace",                data.TraceInfo),
            ("Total context switches", data.TotalSwitches > 0 ? data.TotalSwitches.ToString("N0") : "—"),
            ("Switches per second",    data.SwitchesPerSecond > 0 ? $"{data.SwitchesPerSecond:F0} /s" : "—"),
            ("Voluntary",              data.VoluntarySwitches > 0
                ? $"{data.VoluntarySwitches:N0}  ({data.VoluntaryPct:F1}%)" : "—"),
            ("Preempted",              data.PreemptedSwitches > 0
                ? $"{data.PreemptedSwitches:N0}  ({data.PreemptedPct:F1}%)" : "—"),
            ("CPU idle time",          data.IdleSwitches > 0
                ? $"{data.IdleSwitches:N0} idle wake-ups  •  {FormatDuration(data.IdleRunMs)}  " +
                  $"({(data.TraceSpanMs > 0 ? data.IdleRunMs * 100.0 / data.TraceSpanMs : 0):F1}% of trace span)"
                : "—"),
            ("Trace span",             data.TraceSpanMs > 0 ? FormatDuration(data.TraceSpanMs) : "—"),
            ("Process filter",         data.FilteredProcess ?? "(all processes)"),
        ]);

        if (!data.HasData)
        {
            sink.Alert(AlertLevel.Warning, "No CSwitch events found in trace.",
                "Context switch analysis requires kernel CSwitch events.",
                "PerfView:  collect with /KernelEvents:ContextSwitch  (or /KernelEvents:Default)\n" +
                "xperf:     xperf -on PROC_THREAD+LOADER+CSWITCH\n\n" +
                "Note: .nettrace files collected by dotnet-trace do NOT include kernel CSwitch events.\n" +
                "Use PerfView or xperf to collect an ETL trace with kernel events enabled.");
            return;
        }

        // ── Alerts ────────────────────────────────────────────────────────────
        if (data.PreemptedPct >= 40)
            sink.Alert(AlertLevel.Critical,
                $"High preemption rate: {data.PreemptedPct:F1}% of switches were preempted.",
                "More than 40% of context switches occurred because a thread's CPU quantum expired or was displaced by a higher-priority thread. " +
                "This is a strong indicator of CPU saturation. The process is doing more CPU work than the available cores can service.",
                "Profile CPU with cpu-trace to identify hot methods. Reduce algorithmic complexity. " +
                "Scale out (more instances) or reduce serialization of CPU-bound work.");
        else if (data.PreemptedPct >= 20)
            sink.Alert(AlertLevel.Warning,
                $"Elevated preemption rate: {data.PreemptedPct:F1}% of switches were preempted.",
                "Above 20% preemption suggests CPU pressure is significant but not critical yet.",
                "Cross-reference with cpu-trace hot-path to identify CPU-bound methods.");

        // Detect short average run slices (spinning / chatty wake-ups)
        if (data.TopThreads.Count > 0)
        {
            var avgSlice = data.TopThreads
                .Where(t => t.SwitchCount >= 100 && t.TotalRunMs > 0)
                .Select(t => t.AvgRunSliceMs)
                .DefaultIfEmpty(double.MaxValue)
                .Min();

            if (avgSlice < 0.5 && avgSlice < double.MaxValue)
                sink.Alert(AlertLevel.Warning,
                    $"Very short CPU run slices detected (min avg {avgSlice:F2} ms).",
                    "Some active threads are being switched extremely quickly — average CPU burst under 0.5 ms. " +
                    "This pattern occurs with busy-wait / spin-wait loops, very high-frequency wake-ups (timer storms), " +
                    "or severe lock contention where threads immediately block after waking.",
                    "Identify the top threads with short run slices in the table below. " +
                    "Check contention-trace for lock hotspots. Look for SpinWait or Thread.Sleep(0) in hot paths.");
        }

        // Detect dominant wait reasons
        if (data.WaitReasons.Count > 0)
        {
            var top1 = data.WaitReasons[0];
            if (top1.Pct > 50)
            {
                bool isContentionReason =
                    top1.ReasonName.Contains("Resource", StringComparison.OrdinalIgnoreCase) ||
                    top1.ReasonName.Contains("Mutex",    StringComparison.OrdinalIgnoreCase) ||
                    top1.ReasonName.Contains("Lock",     StringComparison.OrdinalIgnoreCase);
                if (isContentionReason)
                    sink.Alert(AlertLevel.Warning,
                        $"Lock/mutex waits dominate: {top1.Pct:F1}% of voluntary switches are '{top1.ReasonName}'.",
                        "More than half of all voluntary thread waits are for a mutex or critical section. " +
                        "This indicates a serialization bottleneck — threads frequently compete for the same lock.",
                        "Run contention-trace to identify the specific lock objects and stack traces.");
            }
        }

        // ── Rate timeline ─────────────────────────────────────────────────────
        if (data.Timeline is { Count: > 1 })
        {
            sink.Section("Switch Rate Over Time", "cswitch-timeline");
            sink.Text($"Context switches per second across the trace (each bucket = 1 second). " +
                      $"Spikes may indicate GC pauses, lock storms, or transient thread-pool pressure.");
        }

        // ── Voluntary / Preempted gauges ──────────────────────────────────────
        sink.Section("Voluntary vs Preempted Breakdown", "cswitch-breakdown");
        sink.Gauges([
            ("Voluntary (blocked on I/O, lock, sleep, queue)", data.VoluntaryPct, "%"),
            ("Preempted (CPU quantum expired or higher priority)", data.PreemptedPct, "%"),
        ], barMax: 100.0);

        // ── Wait reason distribution ──────────────────────────────────────────
        if (data.WaitReasons.Count > 0)
        {
            sink.Section($"Wait Reason Distribution (voluntary switches only)", "cswitch-waitreasons");
            sink.Text("What threads were waiting for when they voluntarily gave up the CPU. " +
                      "Only events with OldThreadState=Waiting are included here.");

            var reasonRows = data.WaitReasons.Select(r => new[]
            {
                r.ReasonName,
                r.Count.ToString("N0"),
                $"{r.Pct:F1}%",
            }).ToList();

            // Donut of top wait reasons
            var reasonSegs = data.WaitReasons.Take(6)
                .Select(r =>
                {
                    string lbl = r.ReasonName;
                    int paren = lbl.IndexOf('(');
                    if (paren > 0) lbl = lbl[..paren].TrimEnd();
                    if (lbl.Length > 28) lbl = lbl[..28] + "…";
                    return (Label: lbl, Value: (double)r.Count);
                })
                .ToList();
            if (reasonSegs.Count > 1)
                sink.DonutChart(reasonSegs, "Wait reason breakdown (top 6)",
                    $"{data.VoluntarySwitches:N0}\nvoluntary");

            sink.Table(
                ["Wait Reason", "Count", "% of Voluntary"],
                reasonRows,
                "Kernel wait reason for threads that voluntarily blocked. " +
                "WrQueue = thread-pool idle; WrResource = mutex/critical-section; " +
                "DelayExecution = sleep/timer; PageIn = page fault I/O.");
        }

        // ── Top threads ───────────────────────────────────────────────────────
        if (data.TopThreads.Count > 0)
        {
            int showTop = Math.Min(data.TopThreads.Count, top);
            sink.Section($"Top Threads by Context Switches (top {showTop})", "cswitch-threads");
            sink.Text("Context switch count measures scheduling frequency. " +
                      "Avg run slice is the mean CPU burst per switch — very short slices indicate spinning or high-frequency wakeups.");

            var threadRows = data.TopThreads.Take(top).Select(t => new[]
            {
                t.ProcessName.Length > 0 ? t.ProcessName : "(unknown)",
                t.ThreadId.ToString(),
                t.SwitchCount.ToString("N0"),
                $"{t.PctOfAllSwitches:F1}%",
                t.TotalRunMs > 0 ? $"{t.TotalRunMs:F0} ms" : "—",
                t.AvgRunSliceMs > 0 ? $"{t.AvgRunSliceMs:F2} ms" : "—",
                t.VoluntaryCount > 0 ? $"{t.VoluntaryCount:N0}" : "—",
                t.PreemptedCount > 0 ? $"{t.PreemptedCount:N0}" : "—",
            }).ToList();

            sink.Table(
                ["Process", "Thread ID", "Switches", "% Total", "CPU Run Time", "Avg Slice", "Voluntary", "Preempted"],
                threadRows,
                $"Top {threadRows.Count} threads ordered by context switch count. " +
                "Short avg slice + high switch count = spinning or contention. " +
                "Long avg slice + low switch count = I/O-bound or sleeping.");
        }

        // ── Collection guidance ───────────────────────────────────────────────
        if (!data.HasData || data.TotalSwitches < 1000)
            sink.Alert(AlertLevel.Info,
                "Limited CSwitch data. Re-collect with kernel events for full coverage.",
                "PerfView:  PerfView.exe /KernelEvents:ContextSwitch /NoGui collect\n" +
                "xperf:     xperf -on PROC_THREAD+LOADER+CSWITCH -f kernel.etl stop -merge kernel.etl merged.etl\n\n" +
                "Note: dotnet-trace (.nettrace) files do not include kernel context switch events.");
    }

    private static string FormatDuration(double ms)
    {
        if (ms >= 3_600_000) return $"{ms / 3_600_000:F1} h";
        if (ms >= 60_000)    return $"{ms / 60_000:F1} min";
        if (ms >= 1_000)     return $"{ms / 1_000:F1} s";
        return $"{ms:F0} ms";
    }
}
