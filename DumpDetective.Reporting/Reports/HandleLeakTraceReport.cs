using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class HandleLeakTraceReport
{
    public void Render(HandleLeakTraceData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "GCHandle leak analysis from GCHandle/Created and GCHandle/Destroyed events — tracks net handle growth over the trace to detect handle leaks.",
            why: "GCHandle leaks keep managed objects alive indefinitely by pinning them outside the GC graph. This inflates the live set, prevents collection, and can cause heap fragmentation.",
            impact: "Each leaked Pinned handle immobilizes a managed object in the heap, blocking the GC compactor and forcing fragmented LOH-like behavior in SOH.",
            bullets: [
                "Net handle growth   — created minus destroyed handles",
                "Handle kind breakdown — which handle types are accumulating",
                "Growth timeline     — whether growth is accelerating"
            ],
            action: "Ensure GCHandle.Free() is called on all allocated GCHandle values. " +
                    "Prefer using SafeHandle subclasses over raw GCHandle. " +
                    "Use 'handle-table' memory dump analysis to confirm leaked handles."
        );

        sink.Section("Trace Summary", "handle-summary");
        sink.KeyValues([
            ("Trace",             data.TraceInfo),
            ("Total created",     data.TotalCreated.ToString("N0")),
            ("Total destroyed",   data.TotalDestroyed.ToString("N0")),
            ("Net growth",        data.NetGrowth > 0
                                   ? $"+{data.NetGrowth} ⚠" : data.NetGrowth.ToString()),
            ("Leak suspected",    data.IsGrowing ? "Yes ⚠" : "No"),
            ("Process filter",    data.FilteredProcess ?? "(all processes)"),
        ]);

        if (!data.HasData)
        {
            sink.Alert(AlertLevel.Info, "No GCHandle events found.",
                "Collect with: --providers 'Microsoft-Windows-DotNETRuntime:0x4000:4' (GCHandleKeyword)");
            return;
        }

        if (data.IsGrowing)
            sink.Alert(AlertLevel.Warning,
                $"GCHandle leak suspected — net growth of {data.NetGrowth} handles.",
                "More handles were created than destroyed. Unreleased handles keep managed objects alive.",
                "Call GCHandle.Free() in a finally block or implement IDisposable.");

        if (data.GrowthTimeline is { Count: > 2 } tl)
            sink.Sparkline(tl, "Net active handles over time", "");

        if (data.TypeBreakdown.Count > 0)
        {
            sink.Section("Handle Kind Breakdown", "handle-kinds");
            var rows = new List<string[]>(Math.Min(top, data.TypeBreakdown.Count));
            foreach (var k in data.TypeBreakdown.Take(top))
                rows.Add([k.HandleKind, k.Created.ToString("N0"), k.Destroyed.ToString("N0"),
                           k.NetGrowth > 0 ? $"+{k.NetGrowth}" : k.NetGrowth.ToString()]);
            sink.Table(
                ["Handle Kind", "Created", "Destroyed", "Net Growth"],
                rows, "Ordered by net growth descending");
        }
    }
}
