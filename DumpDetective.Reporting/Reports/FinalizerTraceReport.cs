using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class FinalizerTraceReport
{
    public void Render(FinalizerTraceData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "Finalizer queue analysis from GCFinalizeObject and GC suspension events — measures finalization burst duration and tracks types with the most finalizer activity.",
            why: "Excessive finalization pressure delays GC completion, extends application pauses, and can cause memory leaks when the finalizer thread cannot keep up with the queue.",
            impact: "A saturated finalizer queue forces the GC to hold objects in memory longer than necessary, inflating Gen2 size and triggering more frequent full GCs.",
            bullets: [
                "Finalizer bursts   — periods of unusually high finalization activity",
                "Top finalizer types — types generating the most finalization events",
                "Queue growth        — whether the queue is growing across the trace"
            ],
            action: "Implement IDisposable on types with finalizers and call Dispose explicitly. " +
                    "Avoid finalizers when possible — prefer SafeHandle for unmanaged resources."
        );

        sink.Section("Trace Summary", "finalizer-summary");
        sink.KeyValues([
            ("Trace",                     data.TraceInfo),
            ("Total finalization events",  data.TotalFinalizationEvents.ToString("N0")),
            ("GCs with finalizer bursts",  data.GcCountWithFinalizers.ToString("N0")),
            ("Max burst duration",         $"{data.MaxFinalizerBurstMs:F1} ms"),
            ("Avg burst duration",         $"{data.AvgFinalizerBurstMs:F1} ms"),
            ("Queue growing",              data.IsQueueGrowing ? "Yes ⚠" : "No"),
            ("Process filter",             data.FilteredProcess ?? "(all processes)"),
        ]);

        if (!data.HasData)
        {
            sink.Alert(AlertLevel.Info, "No GCFinalizeObject events found.",
                "Finalizer events require --providers 'Microsoft-Windows-DotNETRuntime:0x1:5' (GCKeyword).",
                "dotnet-trace: --providers 'Microsoft-Windows-DotNETRuntime:0x1:4'");
            return;
        }

        if (data.IsQueueGrowing)
            sink.Alert(AlertLevel.Warning,
                "The finalizer queue appears to be growing across the trace.",
                "The finalizer thread is not keeping up with the rate of finalizable object creation.",
                "Profile which types have finalizers. Ensure Dispose() is called to suppress finalization.");

        if (data.MaxFinalizerBurstMs > 100)
            sink.Alert(AlertLevel.Warning,
                $"Long finalizer burst detected: {data.MaxFinalizerBurstMs:F1} ms.",
                "Finalization is blocking GC completion. Application may experience GC pause spikes.");

        if (data.BurstTimeline is { Count: > 2 } tl)
            sink.Sparkline(tl, "Finalizer burst duration over time (ms per GC)", " ms");

        if (data.TopFinalizerTypes.Count > 0)
        {
            sink.Section("Top Finalizer Types", "finalizer-types");
            var rows = new List<string[]>(data.TopFinalizerTypes.Count);
            foreach (var t in data.TopFinalizerTypes.Take(top))
                rows.Add([t.TypeName, t.Count.ToString("N0"), $"{t.PctOfTotal:F1}%"]);
            sink.Table(
                ["Type", "Finalization Count", "% of Burst Time"],
                rows, "Types ordered by finalization event count");
        }

        if (data.FinalizerBursts.Count > 0)
        {
            sink.Section("Worst Finalization Bursts", "finalizer-bursts");
            var rows = new List<string[]>(Math.Min(top, data.FinalizerBursts.Count));
            foreach (var b in data.FinalizerBursts.Take(top))
                rows.Add([$"{b.GcIndex}", b.TopType, $"{b.BurstDurationMs:F1} ms",
                           b.FinalizerCount.ToString("N0"), $"{b.SuspendStartMs:F0} ms"]);
            sink.Table(
                ["GC Index", "Top Type", "Burst Duration", "Finalizer Count", "Suspend Offset"],
                rows, "Ordered by burst duration descending");
        }
    }
}
