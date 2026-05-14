using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class DeadlockPatternReport
{
    public void Render(DeadlockPatternData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "Deadlock heuristic analysis from ContentionStart and WaitHandleWaitStart events — detects thread pairs that remain mutually blocked for more than 5 seconds at different wait sites.",
            why: "A true deadlock halts progress entirely. Heuristic detection from a live trace allows early warning before a full hang occurs.",
            impact: "Even a brief deadlock causes request timeouts and forces thread pool expansion, which amplifies memory and CPU overhead.",
            bullets: [
                "Suspected deadlocks — thread pairs with circular or prolonged mutual waits",
                "Long waits          — threads waiting > 5 s on a single site",
                "Wait chains         — the sequence of lock acquisitions leading to contention"
            ],
            action: "Confirm with 'deadlock-detection' command on a memory dump. " +
                    "Standardize lock acquisition order. Replace Monitor.Enter with async-safe alternatives (SemaphoreSlim, Channel, etc.)."
        );

        sink.Section("Trace Summary", "deadlock-summary");
        sink.KeyValues([
            ("Trace",                  data.TraceInfo),
            ("Suspected deadlocks",    data.SuspectedDeadlockCount > 0
                                        ? $"{data.SuspectedDeadlockCount} ⚠" : "0"),
            ("Long waits (>5 s)",      data.LongWaitCount.ToString("N0")),
            ("Max wait duration",      $"{data.MaxWaitMs:F0} ms"),
            ("Total wait time",        $"{data.TotalWaitMs:F0} ms"),
            ("Process filter",         data.FilteredProcess ?? "(all processes)"),
        ]);

        if (!data.HasData)
        {
            sink.Alert(AlertLevel.Info, "No contention/wait events found.",
                "Collect with: --providers 'Microsoft-Windows-DotNETRuntime:0x300:5' (Contention + Threading)");
            return;
        }

        if (data.SuspectedDeadlockCount > 0)
            sink.Alert(AlertLevel.Critical,
                $"{data.SuspectedDeadlockCount} suspected deadlock pattern(s) detected.",
                "Thread pairs were found mutually blocked for >5 seconds at different wait sites.",
                "Take a memory dump during the hang and run 'deadlock-detection' for confirmation.");

        if (data.WaitChains.Count > 0)
        {
            sink.Section("Wait Chains", "deadlock-chains");
            var rows = new List<string[]>(Math.Min(top, data.WaitChains.Count));
            foreach (var w in data.WaitChains.Take(top))
                rows.Add([$"T{w.Thread1Id} ↔ T{w.Thread2Id}", $"{w.OverlapMs:F0} ms",
                           w.Thread1Frame, w.Thread2Frame]);
            sink.Table(
                ["Thread Pair", "Overlap Duration", "Thread 1 Frame", "Thread 2 Frame"],
                rows, "Wait chains ordered by overlap duration descending");
        }
    }
}
