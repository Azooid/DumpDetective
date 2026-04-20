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
        sink.KeyValues([
            ("Total threads",   data.TotalCount.ToString("N0")),
            ("Alive",           data.AliveCount.ToString("N0")),
            ("Likely blocked",  data.BlockedCount.ToString("N0")),
            ("With exception",  data.WithExceptionCount.ToString("N0")),
            ("GC cooperative",  gcCoop.ToString("N0")),
            ("Named threads",   data.NamedCount.ToString("N0")),
        ]);

        // Category breakdown table (matches original ThreadAnalysisCommand.RenderSummary)
        var categories = data.Threads
            .GroupBy(t => t.Category)
            .OrderByDescending(g => g.Count())
            .Select(g => new[] { g.Key, g.Count().ToString("N0") })
            .ToList();
        if (categories.Count > 1)
            sink.Table(["Category", "Count"], categories, "Thread categories");

        if (data.BlockedCount >= data.TotalCount / 2 && data.TotalCount > 4)
            sink.Alert(AlertLevel.Critical, $"{data.BlockedCount} of {data.TotalCount} threads are blocked.",
                "Check for deadlocks — run deadlock-detection for wait-chain analysis.");
        else if (data.BlockedCount > 0)
            sink.Alert(AlertLevel.Warning, $"{data.BlockedCount} thread(s) appear blocked on synchronization primitives.");

        // Apply filters
        var filtered = data.Threads.AsEnumerable();
        if      (stateFilter == "blocked")  filtered = filtered.Where(t => t.LockInfo is not null);
        else if (stateFilter == "running")  filtered = filtered.Where(t => t.IsAlive && t.LockInfo is null);
        else if (stateFilter == "dead")     filtered = filtered.Where(t => !t.IsAlive);
        if (blockedOnly)                    filtered = filtered.Where(t => t.LockInfo is not null);
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
