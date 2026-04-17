using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class WeakRefsReport
{
    public void Render(WeakRefsData data, IRenderSink sink, bool showAddr = false)
    {
        int aliveCount     = data.Handles.Count(r => r.Alive);
        int collectedCount = data.Handles.Count - aliveCount;
        int total          = data.Handles.Count;
        int alivePercent   = total > 0 ? aliveCount * 100 / total : 0;

        RenderHandleSummary(sink, total, aliveCount, alivePercent, collectedCount);
        RenderHandleTypeBreakdown(data, sink);
        RenderCollectedHandles(data, sink, alivePercent, collectedCount);
        if (showAddr) RenderAddressTable(data, sink);
        RenderConditionalWeakTables(data, sink);
    }

    private static void RenderHandleSummary(IRenderSink sink,
        int total, int aliveCount, int alivePercent, int collectedCount)
    {
        sink.Section("Weak Reference Summary");
        sink.KeyValues([
            ("Total weak handles", total.ToString("N0")),
            ("Alive",              $"{aliveCount:N0}  ({alivePercent}%)"),
            ("Collected",          collectedCount.ToString("N0")),
        ]);

        if (total > 10 && alivePercent < 20)
            sink.Alert(AlertLevel.Warning,
                $"Only {alivePercent}% of weak references are alive — high object churn or abandoned caches.",
                advice: "Review WeakReference<T> / ConditionalWeakTable usage. Objects may be collected sooner than expected.");
        else if (total > 1000)
            sink.Alert(AlertLevel.Info, $"{total:N0} weak handles in use.");
    }

    private static void RenderHandleTypeBreakdown(WeakRefsData data, IRenderSink sink)
    {
        var rows = data.Handles.Where(r => r.Alive)
            .GroupBy(r => r.Type)
            .OrderByDescending(g => g.Count())
            .Take(30)
            .Select(g => new[] { g.Key, g.Count().ToString("N0") })
            .ToList();
        if (rows.Count > 0)
            sink.Table(["Alive Object Type", "Count"], rows, "Top types currently alive via weak reference");
    }

    private static void RenderCollectedHandles(WeakRefsData data, IRenderSink sink,
        int alivePercent, int collectedCount)
    {
        if (collectedCount == 0) return;

        int byShort = data.Handles.Count(r => !r.Alive && r.Kind == "WeakShort");
        int byLong  = data.Handles.Count(r => !r.Alive && r.Kind == "WeakLong");

        sink.KeyValues([
            ("Collected (WeakShort)", $"{byShort:N0}  (tracking-resurrection disabled)"),
            ("Collected (WeakLong)",  $"{byLong:N0}  (tracking-resurrection enabled)"),
            ("Note", "Collected object types are unavailable — object graphs were reclaimed by GC"),
        ]);

        if (byLong > byShort && byLong > 100)
            sink.Alert(AlertLevel.Info,
                $"{byLong:N0} WeakLong handles with collected targets — these prevent resurrection tracking.",
                "WeakLong handles keep the object's finalizer-resurrection reference alive longer than needed.",
                "Prefer WeakReference<T>(trackResurrection: false) unless you need post-finalize tracking.");
    }

    private static void RenderAddressTable(WeakRefsData data, IRenderSink sink)
    {
        if (data.Handles.Count == 0) return;
        var rows = data.Handles.Take(200).Select(r => new[]
        {
            r.Kind, r.Alive ? "Alive" : "Collected", r.Type, $"0x{r.Addr:X16}",
        }).ToList();
        sink.Table(["Kind", "Status", "Type", "Address"], rows, $"First {rows.Count} handles");
    }

    private static void RenderConditionalWeakTables(WeakRefsData data, IRenderSink sink)
    {
        if (data.ConditionalWeakTables.Count == 0) return;
        sink.Section("ConditionalWeakTable Instances");
        sink.KeyValues([("Total ConditionalWeakTable instances", data.ConditionalWeakTables.Count.ToString("N0"))]);

        var rows = data.ConditionalWeakTables
            .GroupBy(c => c.TypeParam)
            .OrderByDescending(g => g.Sum(c => c.Entries))
            .Take(20)
            .Select(g => new[]
            {
                g.Key.Length > 0 ? g.Key : "<unparameterized>",
                g.Count().ToString("N0"),
                g.Sum(c => c.Entries).ToString("N0"),
            })
            .ToList();
        sink.Table(["Type Parameters", "Instances", "Total Entries"], rows,
            "Large entry counts may indicate per-object metadata leaks");

        int largeCount = data.ConditionalWeakTables.Count(c => c.Entries > 10000);
        if (largeCount > 0)
            sink.Alert(AlertLevel.Warning,
                $"{largeCount} ConditionalWeakTable instance(s) with > 10,000 entries.",
                "Very large CWT tables can indicate metadata is accumulating per object instance.",
                "Audit usages that store per-object data in static CWT fields.");
    }
}
