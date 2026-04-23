using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class DeadlockReport
{
    public void Render(DeadlockData data, IRenderSink sink)
    {
        int monitorWaiters = data.MonitorLocks.Sum(l => l.WaiterManagedIds.Count);
        int contested      = data.MonitorLocks.Count(l => l.WaiterManagedIds.Count > 0);

        sink.Section("Analysis Summary");
        sink.Explain(
            what: "Deadlock detection using Monitor (lock) ownership data from the process's SyncBlock table. " +
                  "A deadlock occurs when Thread A holds Lock X and waits for Lock Y, while Thread B holds Lock Y and waits for Lock X.",
            why:  "A single deadlock causes a complete, permanent application hang for all operations sharing those locks. " +
                  "The application appears running (CPU may be 0%) but cannot process any requests involving the deadlocked code path.",
            bullets:
            [
                "Confirmed cycle \u2192 actual deadlock: two or more threads are in a circular wait, none can proceed",
                "Contested lock with no cycle \u2192 lock contention (not deadlock): the owner will eventually release the lock",
                "Many contested locks with no deadlock \u2192 may indicate serialization bottleneck under high concurrency",
                "Independent waiters \u2192 normal: threads waiting on events, I/O, queues (not lock-related)",
            ],
            impact: "A confirmed deadlock means those threads will never make progress without a process restart. " +
                    "Any request that tries to acquire one of the deadlocked locks will also hang indefinitely.",
            action: "For confirmed cycles: identify the lock-acquisition order in the involved code. " +
                    "Enforce a consistent acquisition order (always acquire Lock A before Lock B). " +
                    "Consider async alternatives with cancellation timeouts (SemaphoreSlim.WaitAsync with CancellationToken).");
        sink.KeyValues([
            ("Threads total",           data.TotalThreadsByRuntime.ToString("N0")),
            ("Inflated monitor locks",  data.MonitorLocks.Count.ToString("N0")),
            ("Contested locks",         contested.ToString("N0")),
            ("Monitor waiters",         monitorWaiters.ToString("N0")),
            ("Independent waiters",     data.IndependentWaiters.Count.ToString("N0")),
            ("Confirmed deadlock cycles", data.ConfirmedCycles.Count.ToString("N0")),
            ("Named threads found",     data.NamedThreadCount.ToString("N0")),
        ]);

        // ── Verdict ──────────────────────────────────────────────────────────
        if (data.ConfirmedCycles.Count > 0)
        {
            sink.Alert(AlertLevel.Critical,
                $"{data.ConfirmedCycles.Count} confirmed deadlock cycle(s) detected.",
                "A circular wait dependency was found: each thread owns a lock while waiting for a lock held by another thread in the cycle.",
                "Enforce consistent lock-acquisition order (lock hierarchy), use SemaphoreSlim with CancellationToken timeouts, or eliminate shared mutable state.");
            RenderCycles(sink, data);
        }
        else if (contested > 0)
        {
            sink.Alert(AlertLevel.Warning,
                $"{contested} contested monitor lock(s) — {monitorWaiters} thread(s) waiting to enter.",
                "No cyclic dependency found. This is lock contention, not a deadlock. The owner thread will release the lock once it finishes its critical section.",
                "Reduce the scope of your lock blocks, consider upgrading to ReaderWriterLockSlim for read-heavy paths, or convert to async/await.");
        }
        else if (data.MonitorLocks.Count > 0)
        {
            sink.Alert(AlertLevel.Info,
                $"{data.MonitorLocks.Count} inflated monitor lock(s) held — no waiters, no deadlock.");
        }
        else if (data.IndependentWaiters.Count > 0)
        {
            sink.Alert(AlertLevel.Info,
                $"{data.IndependentWaiters.Count} thread(s) are in normal waiting states (WaitOne/Task.Wait/etc.).",
                "These threads are independently waiting on events, timers, or queues. This is expected for background workers and infrastructure threads.",
                "No action required unless the application is unresponsive.");
        }
        else
        {
            sink.Alert(AlertLevel.Info, "No monitor contention or deadlock indicators found.");
            return;
        }

        // ── Sections ─────────────────────────────────────────────────────────
        if (data.MonitorLocks.Count > 0)
            RenderMonitorLocks(sink, data);

        if (data.IndependentWaiters.Count > 0)
            RenderIndependentWaiters(sink, data);
    }

    // ── Confirmed cycles ─────────────────────────────────────────────────────

    private static void RenderCycles(IRenderSink sink, DeadlockData data)
    {
        sink.Section("Deadlock Cycles");
        for (int i = 0; i < data.ConfirmedCycles.Count; i++)
        {
            var cycle = data.ConfirmedCycles[i];
            string chain = string.Join(" → ", cycle.ThreadIds.Select(id => $"T{id}"));
            sink.Alert(AlertLevel.Critical, $"Cycle {i + 1}: {chain}");
        }
    }

    // ── Monitor locks table ───────────────────────────────────────────────────

    private static void RenderMonitorLocks(IRenderSink sink, DeadlockData data)
    {
        sink.Section("Monitor Lock Table");
        sink.Text(
            "Source: sync block table (inflated monitors only). " +
            "An inflated lock means ≥1 thread has entered or is waiting to enter a Monitor.Enter / lock() block.");

        var rows = data.MonitorLocks
            .OrderByDescending(l => l.WaiterManagedIds.Count)
            .Select(l =>
            {
                string ownerCell = l.OwnerManagedId.HasValue
                    ? $"T{l.OwnerManagedId}" + (l.OwnerThreadName is not null ? $" [{l.OwnerThreadName}]" : "")
                    : "(unknown)";
                string waitersCell = l.WaiterManagedIds.Count > 0
                    ? string.Join(", ", l.WaiterManagedIds.Select(id => $"T{id}"))
                    : "—";
                return new[]
                {
                    l.LockAddress == 0 ? "—" : $"0x{l.LockAddress:X}",
                    l.LockTypeName,
                    ownerCell,
                    waitersCell,
                    l.WaiterManagedIds.Count.ToString("N0"),
                    l.RecursionCount > 1 ? l.RecursionCount.ToString() : "",
                };
            })
            .ToList();

        sink.Table(
            ["Lock Object", "Type", "Owner Thread", "Waiting Threads", "Waiter Count", "Recursion"],
            rows,
            "Monitor locks from the sync-block table");
    }

    // ── Independent waiters ───────────────────────────────────────────────────

    private static void RenderIndependentWaiters(IRenderSink sink, DeadlockData data)
    {
        sink.Section("Independent Waiting Threads");
        sink.Text(
            "These threads are waiting on WaitHandle/Task/SemaphoreSlim/etc. primitives. " +
            "They are NOT waiting on each other — this is normal infrastructure behaviour " +
            "(timer threads, background workers, APM dispatchers). " +
            "They do NOT indicate a deadlock.");

        var rows = data.IndependentWaiters
            .OrderBy(w => w.BlockReason)
            .ThenBy(w => w.ManagedId)
            .Select(w => new[]
            {
                $"T{w.ManagedId}",
                $"0x{w.OSThreadId:X4}",
                w.ThreadName ?? "",
                w.BlockReason,
                w.TopUserFrame,
            })
            .ToList();

        sink.Table(["Mgd ID", "OS ID", "Thread Name", "Wait Primitive", "Top User Frame"], rows);

        // Stack traces (collapsed by default — these are INFO-level)
        sink.Section("Independent Waiter Stack Traces");
        foreach (var w in data.IndependentWaiters)
        {
            string title = $"T{w.ManagedId}" +
                (w.ThreadName is not null ? $" [{w.ThreadName}]" : "") +
                $"  {w.BlockReason}";
            sink.BeginDetails(title, open: false);
            sink.Table(["Frame"], w.StackFrames.Select(f => new[] { f }).ToList());
            sink.EndDetails();
        }
    }
}
