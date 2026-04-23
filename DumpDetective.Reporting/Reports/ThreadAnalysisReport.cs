using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

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
            sink.Table(["Category", "Count"], categories, "Thread categories");

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
                    t.StackFrames.Select((f, i) => new[] { i.ToString(), f }).ToList());
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
