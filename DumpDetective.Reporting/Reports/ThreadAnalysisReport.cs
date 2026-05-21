using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class ThreadAnalysisReport
{
    public void Render(ThreadAnalysisData data, IRenderSink sink,
        bool showStacks = false, bool blockedOnly = false,
        string? nameFilter = null, string? stateFilter = null)
    {
        int gcCoop = data.Threads.Count(t => t.GcMode == "Cooperative");
        sink.Section("Thread Summary");
        sink.Explain(
            what: "All managed threads in the process at the time of capture: state, wait reason, stack category, " +
                  "and current exception status.",
            why:  "Thread state reveals execution health. Monitor-blocked threads are waiting for a lock someone else holds. " +
                  "Independent waiters are doing normal async work. Many blocked threads means lock contention or potential deadlock.",
            bullets:
            [
                "Monitor-blocked \u2192 waiting to enter a lock (run 'deadlock-detection <dump>' to check for cyclic waits)",
                "Independent waiting \u2192 expected behavior: timers, event loops, I/O completions, background workers",
                "With exception \u2192 thread has an active unhandled exception at time of capture",
                "GC cooperative mode \u2192 thread is in managed code and participating in GC suspension",
                "High total thread count \u2192 investigate for thread leaks (threads created but not stopped)",
            ],
            impact: "Blocked threads reduce available parallelism. If all thread pool workers are blocked, " +
                    "the pool must spin up new threads (slow) or queue work indefinitely (latency). " +
                    "Deadlocked threads permanently reduce the thread pool until the process restarts.",
            action: "If MonitorBlockedCount is high, run 'deadlock-detection <dump>' immediately. " +
                    "Run 'thread-pool <dump>' for thread pool queue depth and 'async-stacks <dump>' for async backlog.");
        sink.KeyValues([
            ("Total threads",       data.TotalCount.ToString("N0")),
            ("Alive",               data.AliveCount.ToString("N0")),
            ("Monitor-blocked",     data.MonitorBlockedCount.ToString("N0")),
            ("Independently waiting", data.IndependentWaitCount.ToString("N0")),
            ("With exception",      data.WithExceptionCount.ToString("N0")),
            ("GC cooperative",      gcCoop.ToString("N0")),
            ("Named threads",       data.NamedCount.ToString("N0")),
        ]);

        // Category breakdown table
        var categories = data.Threads
            .GroupBy(t => t.Category)
            .OrderByDescending(g => g.Count())
            .Select(g => new[] { g.Key, g.Count().ToString("N0") })
            .ToList();
        if (categories.Count > 1)
        {
            var catSegs = categories
                .Select(r => (Label: r[0], Value: (double)int.Parse(r[1].Replace(",", ""))))
                .ToList();
            sink.DonutChart(catSegs, "Thread count by category",
                $"{data.TotalCount}\nthreads");
            sink.Table(["Category", "Count"], categories, "Thread categories");
        }

        // Blocked / waiting health gauges
        if (data.TotalCount > 0 && (data.MonitorBlockedCount > 0 || data.IndependentWaitCount > 0))
        {
            double blockedPct = data.MonitorBlockedCount * 100.0 / data.TotalCount;
            double waitingPct = data.IndependentWaitCount * 100.0 / data.TotalCount;
            sink.Gauges([
                ("Monitor-blocked",      blockedPct, "%"),
                ("Independently waiting", waitingPct, "%"),
            ], barMax: 100);
        }

        if (data.MonitorBlockedCount >= data.TotalCount / 2 && data.TotalCount > 4)
            sink.Alert(AlertLevel.Critical,
                $"{data.MonitorBlockedCount} of {data.TotalCount} threads are waiting to enter a Monitor lock.",
                "This many contested locks is unusual — likely deadlock or severe contention. Run deadlock-detection for ownership analysis.");
        else if (data.MonitorBlockedCount > 0)
            sink.Alert(AlertLevel.Warning,
                $"{data.MonitorBlockedCount} thread(s) are waiting to enter a Monitor lock (contended lock block).",
                "Run deadlock-detection to check for cyclic wait chains.");
        if (data.IndependentWaitCount > 0)
            sink.Alert(AlertLevel.Info,
                $"{data.IndependentWaitCount} thread(s) are in independent waiting states (WaitHandle/Task.Wait/Semaphore/etc.).",
                "These are normal background workers, timers, and APM dispatchers — not an indication of deadlock.");

        // Apply filters
        var filtered = data.Threads.AsEnumerable();
        if      (stateFilter == "blocked")  filtered = filtered.Where(t => t.WaitKind == WaitKind.Monitor);
        else if (stateFilter == "waiting")  filtered = filtered.Where(t => t.WaitKind == WaitKind.Independent);
        else if (stateFilter == "running")  filtered = filtered.Where(t => t.IsAlive && t.WaitKind == WaitKind.None);
        else if (stateFilter == "dead")     filtered = filtered.Where(t => !t.IsAlive);
        if (blockedOnly)                    filtered = filtered.Where(t => t.WaitKind == WaitKind.Monitor);
        if (nameFilter is not null)
            filtered = filtered.Where(t => t.Name?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) == true);

        var toShow = filtered.ToList();

        string title = stateFilter is not null and not "all"
            ? $"Threads — state={stateFilter} ({toShow.Count})"
            : blockedOnly ? $"Blocked Threads ({toShow.Count})"
            : $"All Threads ({toShow.Count})";

        if (!showStacks || toShow.All(t => t.StackFrames.Count == 0))
            RenderThreadTable(sink, toShow, title);
        else
            RenderThreadCards(sink, toShow, title);

        RenderExceptionDetails(sink, toShow);
        RenderStackMemory(sink, data);
    }

    private static void RenderStackMemory(IRenderSink sink, ThreadAnalysisData data)
    {
        if (data.TotalStackCommitted <= 0) return;

        sink.Section("Stack Memory");
        sink.Explain(
            what: "Thread stack virtual address space committed at the time of capture.",
            why:  "Each thread has a dedicated stack (default 1 MB on Windows for .NET). " +
                  "Hundreds of threads each committing 512 KB–1 MB can silently consume several hundred MB of " +
                  "virtual address space — a significant pressure source distinct from the managed heap.",
            bullets:
            [
                "TotalStackCommitted = StackBase \u2212 StackLimit summed across all alive threads",
                "Thread count \u00d7 1 MB typical stack = rough upper bound; actual depends on stack depth",
                "High stack consumption per thread \u2192 deep recursion or very deep call chains",
            ]);

        sink.KeyValues([
            ("Total stack committed", DumpHelpers.FormatSize(data.TotalStackCommitted)),
            ("Alive threads",         data.AliveCount.ToString("N0")),
            ("Avg per thread",        DumpHelpers.FormatSize(data.AliveCount > 0 ? data.TotalStackCommitted / data.AliveCount : 0)),
        ]);

        if (data.TotalStackCommitted > 200_000_000)
            sink.Alert(AlertLevel.Warning,
                $"Thread stacks commit {DumpHelpers.FormatSize(data.TotalStackCommitted)} — " +
                "high thread count is consuming significant virtual address space.",
                advice: "Reduce thread count using async I/O and thread pool patterns instead of per-request dedicated threads.");

        // Per-thread stack size table (only threads with data, top 30 by size)
        var withStack = data.Threads
            .Where(t => t.IsAlive && t.StackCommitted > 0)
            .OrderByDescending(t => t.StackCommitted)
            .Take(30)
            .ToList();

        if (withStack.Count == 0) return;

        sink.BeginDetails($"Per-thread stack sizes  —  top {withStack.Count} alive threads", open: false);
        sink.Table(
            ["Mgd ID", "OS ID", "Thread Name", "Category", "Stack Committed"],
            withStack.Select(t => new[]
            {
                t.ManagedId.ToString(),
                $"{t.OSThreadId}",
                t.Name ?? "",
                t.Category,
                DumpHelpers.FormatSize(t.StackCommitted),
            }).ToList());
        sink.EndDetails();
    }

    private static void RenderThreadTable(IRenderSink sink, IReadOnlyList<ThreadInfo> threads, string title)
    {
        sink.Section(title);
        var rows = threads.Select(t => new[]
        {
            t.ManagedId.ToString(), $"{t.OSThreadId}",
            t.Name ?? "", t.Category, t.GcMode,
            t.Exception ?? "", t.LockInfo ?? "",
        }).ToList();
        sink.Table(["Mgd ID", "OS ID", "Thread Name", "Category", "GC Mode", "Exception", "Waiting On"], rows);
    }

    private static void RenderThreadCards(IRenderSink sink, IReadOnlyList<ThreadInfo> threads, string title)
    {
        sink.Section(title);
        foreach (var t in threads)
        {
            bool blocked = t.LockInfo is not null;
            string detail = $"Thread {t.ManagedId}" +
                (t.Name is not null ? $" [{t.Name}]" : "") +
                $"  OS:{t.OSThreadId}  [{t.Category}]" +
                (blocked ? "  ⚠ BLOCKED" : "") +
                (t.Exception is not null ? $"  ex:{t.Exception}" : "");
            sink.BeginDetails(detail, open: blocked || t.Exception is not null);

            if (t.StackFrames.Count > 0)
                sink.Table(["#", "Frame"],
                    t.StackFrames.Select((f, i) => new[] { i.ToString(), DumpHelpers.SanitizeFrame(f) }).ToList());
            else
                sink.Text("  (no managed frames)");

            sink.EndDetails();
        }
    }

    private static void RenderExceptionDetails(IRenderSink sink, IReadOnlyList<ThreadInfo> threads)
    {
        var withEx = threads.Where(t => t.Exception is not null).ToList();
        if (withEx.Count == 0) return;

        sink.Section("Exception Details");
        var rows = withEx.Select(t => new[]
        {
            t.ManagedId.ToString(), t.Exception ?? "", t.Name ?? "",
        }).ToList();
        sink.Table(["Mgd ID", "Exception", "Thread Name"], rows);
    }
}
