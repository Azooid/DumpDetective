using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class FinalizerQueueReport
{
    public void Render(FinalizerQueueData data, IRenderSink sink, int top = 30, bool showAddr = false)
    {
        sink.Section("Finalizer Queue Summary");

        if (data.Total == 0) { sink.Text("Finalizer queue is empty — no finalizable objects found."); return; }

        int critCount = data.Stats.Values.Where(v => v.IsCritical).Sum(v => v.Count);
        int gen2Loh   = data.Stats.Values.Sum(v => v.Gen2 + v.Loh);

        sink.KeyValues([
            ("Objects in finalizer queue", data.Total.ToString("N0")),
            ("Total size (est.)",          DumpHelpers.FormatSize(data.TotalSize)),
            ("Critical finalizer objects", critCount.ToString("N0")),
            ("Gen2 + LOH objects",         gen2Loh.ToString("N0")),
            ("Resurrection candidates",    data.ResurrectionCount.ToString("N0")),
            ("Finalizer thread blocked",   data.FinalizerThreadBlocked ? "⚠ YES" : "no"),
        ]);

        RenderFinalizerThread(sink, data);
        RenderAdvisories(sink, data, gen2Loh, critCount);

        var sorted = data.Stats.OrderByDescending(kv => kv.Value.Size).Take(top).ToList();
        RenderTypeTable(sink, sorted, top, data.Stats.Count);

        if (showAddr)
            RenderAddresses(sink, sorted);
    }

    private static void RenderFinalizerThread(IRenderSink sink, FinalizerQueueData data)
    {
        sink.Section("Finalizer Thread State");
        if (data.FinalizerFrames.Count == 0) { sink.Text("Finalizer thread not found in dump."); return; }

        if (data.FinalizerThreadBlocked)
            sink.Alert(AlertLevel.Critical, "Finalizer thread appears blocked.",
                "A blocked finalizer thread prevents all queued objects from being collected.",
                "Ensure Dispose() does not deadlock. Avoid calling back into the GC from Finalize().");
        else
            sink.Alert(AlertLevel.Info, "Finalizer thread is running normally.");

        sink.Table(["Stack Frame"], data.FinalizerFrames.Select(f => new[] { f }).ToList(),
            "Finalizer thread stack (top 30 frames)");
    }

    private static void RenderAdvisories(IRenderSink sink, FinalizerQueueData data, int gen2Loh, int critCount)
    {
        if (data.Total >= 10_000)
            sink.Alert(AlertLevel.Critical, $"{data.Total:N0} objects pending finalization.",
                "Extreme finalizer queue pressure can block GC and cause OOM.",
                "Ensure Dispose() is called on IDisposable objects. Suppress finalization in constructors after successful Dispose().");
        else if (data.Total >= 1_000)
            sink.Alert(AlertLevel.Warning, $"{data.Total:N0} objects pending finalization.");

        if (gen2Loh > 500)
            sink.Alert(AlertLevel.Warning, $"{gen2Loh:N0} finalizable objects in Gen2/LOH.",
                "Gen2/LOH objects require a full GC plus a second collection to be freed after finalization.",
                "Use Dispose pattern with GC.SuppressFinalize to avoid two-pass collection cost.");

        if (critCount > 0)
            sink.Alert(AlertLevel.Warning, $"{critCount:N0} critical finalizer objects (CriticalFinalizerObject/SafeHandle).",
                "Critical finalizers run in a restricted execution region and must not throw or block.");

        if (data.ResurrectionCount > 0)
            sink.Alert(AlertLevel.Warning, $"{data.ResurrectionCount} possible resurrection candidate(s).",
                "Objects with both a finalizer queue entry and a strong handle may be resurrecting.",
                "Ensure _resurrectedObj = null is set in Finalize if resurrection is not intentional.");
    }

    private static void RenderTypeTable(IRenderSink sink,
        IEnumerable<KeyValuePair<string, FinalizerTypeStats>> sorted, int top, int totalTypes)
    {
        sink.Section($"Top {top} Types by Size");
        var rows = sorted.Select(kv =>
        {
            var s = kv.Value;
            return new[]
            {
                kv.Key,
                s.Count.ToString("N0"),
                DumpHelpers.FormatSize(s.Size),
                $"G0:{s.Gen0} G1:{s.Gen1} G2:{s.Gen2} L:{s.Loh} P:{s.Poh}",
                s.HasDispose ? "✓" : "",
                s.IsCritical ? "✓" : "",
            };
        }).ToList();
        sink.Table(["Type", "Count", "Size", "Gen Distribution", "Dispose", "Critical"], rows,
            $"Top {top} of {totalTypes} types  |  sorted by size");
    }

    private static void RenderAddresses(IRenderSink sink,
        IEnumerable<KeyValuePair<string, FinalizerTypeStats>> sorted)
    {
        sink.Section("Sample Addresses (≤20 per type)");
        foreach (var (typeName, stats) in sorted)
        {
            if (stats.Addresses.Count == 0) continue;
            sink.BeginDetails($"{typeName}  ({stats.Count:N0} in queue)", open: false);
            var rows = stats.Addresses.Select(a => new[] { $"0x{a:X16}" }).ToList();
            sink.Table(["Address"], rows);
            sink.EndDetails();
        }
    }
}
