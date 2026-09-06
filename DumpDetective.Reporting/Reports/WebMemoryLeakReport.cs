using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class WebMemoryLeakReport
{
    private static string SignedPct(double fraction)
    {
        double pct = fraction * 100;
        return pct >= 0 ? $"+{pct:F0}%" : $"{pct:F0}%";
    }

    public void Render(WebMemoryLeakData data, IRenderSink sink)
    {
        sink.Section("Trend");
        sink.Explain(
            what: "JS heap, DOM node count, and event-listener count sampled periodically throughout the recording.",
            why:  "A healthy page's heap/nodes rise and fall with GC (a sawtooth). Listener count should track node " +
                  "count closely — 1 listener per few nodes is normal. Sustained growth in any of these across the " +
                  "whole recording, or a listener count far above node count, means something is never cleaned up.",
            bullets:
            [
                "Listener:node ratio > 3:1 → listeners are being (re)bound without ever being removed",
                "Heap/nodes trending up across the whole recording (not just GC sawtooth) → a JS-side leak",
                "Growth concentrated around specific user actions → that action's cleanup path is the suspect",
            ]);

        sink.MultiSparkline(
        [
            ("JS Heap (MB)",   data.HeapSeriesMb,   "MB"),
            ("DOM Nodes",      data.NodesSeries,     ""),
            ("Event Listeners", data.ListenerSeries, ""),
        ], "Memory trend over the recording");

        sink.KeyValues([
            ("Peak JS heap",        $"{data.MaxHeapBytes / 1024.0 / 1024.0:F1} MB"),
            ("Peak DOM nodes",      data.MaxNodes.ToString("N0")),
            ("Peak event listeners", data.MaxListeners.ToString("N0")),
            ("Listener : node ratio", $"{data.ListenerNodeRatio:F1} : 1"),
            ("Heap growth (first 10% → last 10%)",      SignedPct(data.HeapGrowthFraction)),
            ("Nodes growth (first 10% → last 10%)",     SignedPct(data.NodesGrowthFraction)),
            ("Listener growth (first 10% → last 10%)",  SignedPct(data.ListenerGrowthFraction)),
        ]);

        sink.Section("Findings");
        foreach (var f in data.Findings)
            sink.Alert(
                f.Severity switch
                {
                    FindingSeverity.Critical => AlertLevel.Critical,
                    FindingSeverity.Warning  => AlertLevel.Warning,
                    _                        => AlertLevel.Info,
                },
                f.Headline, f.Detail, f.Advice);

        sink.Section("Next Steps");
        sink.KeyValues([
            ("CPU hotspots for this trace",  "web-cpu-hotspots <trace.json.gz>"),
            ("Collecting a trace",           "Chrome DevTools → Performance panel → record with 'Memory' checkbox enabled → Export (gzip)"),
        ]);
    }
}
