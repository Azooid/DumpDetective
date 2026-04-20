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
            ("Total in queue",              data.Total.ToString("N0")),
            ("Total size estimate",         DumpHelpers.FormatSize(data.TotalSize)),
            ("Distinct types",              data.Stats.Count.ToString("N0")),
            ("Types with Dispose()",        data.Stats.Values.Count(v => v.HasDispose).ToString("N0")),
            ("Critical finalizer objects",  critCount.ToString("N0")),
            ("In Gen2 / LOH (most costly)", gen2Loh.ToString("N0")),
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
        sink.Section("Finalizer Thread");
        if (data.FinalizerFrames.Count == 0) { sink.Text("Finalizer thread not found in dump."); return; }

        string state = data.FinalizerThreadBlocked ? "⚠ BLOCKED"
            : data.FinalizerFrames.Count > 0 ? "Running" : "Idle";

        sink.KeyValues([
            ("Managed thread ID", data.FinalizerThreadId.ToString()),
            ("OS thread ID",      $"0x{data.FinalizerThreadOSId:X}"),
            ("State",             state),
        ]);

        if (data.FinalizerThreadBlocked)
            sink.Alert(AlertLevel.Critical, "Finalizer thread appears blocked.",
                "A blocked finalizer thread prevents all queued objects from being collected.",
                "Ensure Dispose() does not deadlock. Avoid calling back into the GC from Finalize().");

        if (data.FinalizerFrames.Count > 0)
            sink.Table(["#", "Stack Frame"],
                data.FinalizerFrames.Select((f, i) => new[] { i.ToString(), f }).ToList(),
                "Finalizer thread call stack");
        else
            sink.Text("  (no managed frames — finalizer thread is idle or waiting for work)");
    }

    private static void RenderAdvisories(IRenderSink sink, FinalizerQueueData data, int gen2Loh, int critCount)
    {
        sink.Alert(AlertLevel.Info,
            "All objects in the finalizer queue delay GC collection of their entire retained object graph.",
            advice: "Call Dispose() / use 'using' statements to avoid finalizer pressure. Finalizers run on a single dedicated thread.");

        if (data.Total >= 500)
            sink.Alert(AlertLevel.Critical, $"{data.Total:N0} objects pending finalization.",
                advice: "A large finalizer queue indicates heavy GC pressure. Wrap IDisposable objects in 'using'.");
        else if (data.Total >= 100)
            sink.Alert(AlertLevel.Warning, $"{data.Total:N0} objects pending finalization.");

        if (gen2Loh > 0)
            sink.Alert(AlertLevel.Warning,
                $"{gen2Loh:N0} finalizable objects are in Gen2/LOH.",
                "Gen2/LOH objects survived at least 2 GC cycles before landing in the queue.",
                "These cause the most GC overhead — they block segment reclaim until Finalize() completes.");

        if (critCount > 0)
            sink.Alert(AlertLevel.Info,
                $"{critCount:N0} critical finalizer objects (SafeHandle / CriticalFinalizerObject).",
                "These are prioritised by the GC but still block native handle release.",
                "Dispose() SafeHandles explicitly — do not rely on finalization for unmanaged resources.");

        if (data.ResurrectionCount > 0)
            sink.Alert(AlertLevel.Warning,
                $"{data.ResurrectionCount} finalizable object(s) also have strong GC handles — possible resurrection.",
                "Objects that re-register for finalization in Finalize() create resurrection cycles.",
                "Avoid resurrecting objects in Finalize(). Prefer the Dispose pattern (IDisposable + GC.SuppressFinalize).");
    }

    private static void RenderTypeTable(IRenderSink sink,
        IEnumerable<KeyValuePair<string, FinalizerTypeStats>> sorted, int top, int totalTypes)
    {
        sink.Section("Types by Queue Size");
        var rows = sorted.Select(kv =>
        {
            var v = kv.Value;
            long avg = v.Count > 0 ? v.Size / v.Count : 0;
            string gen2flag = (v.Gen2 + v.Loh) > 0 ? $" ⚠{v.Gen2 + v.Loh}" : "";
            return new[]
            {
                kv.Key,
                v.Count.ToString("N0"),
                DumpHelpers.FormatSize(v.Size),
                DumpHelpers.FormatSize(avg),
                $"G0:{v.Gen0} G1:{v.Gen1} G2:{v.Gen2} LOH:{v.Loh}" + gen2flag,
                v.HasDispose ? "✓" : "—",
                v.IsCritical ? "✓" : "—",
            };
        }).ToList();
        sink.Table(
            ["Type", "Count", "Total Size", "Avg Size", "Gen Distribution", "IDisposable", "Critical"],
            rows,
            $"Top {rows.Count} of {totalTypes} types by size — ⚠N = N objects in Gen2/LOH");
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
