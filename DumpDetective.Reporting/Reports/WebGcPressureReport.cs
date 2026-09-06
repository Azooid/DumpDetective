using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class WebGcPressureReport
{
    public void Render(WebGcPressureData data, IRenderSink sink)
    {
        sink.Section("GC Pressure");
        sink.Explain(
            what: "V8 garbage collection cycles captured during the recording — major (full-heap, 'Mark-Compactor') " +
                  "and minor (young-generation, 'Scavenger') collections, with their pause durations.",
            why:  "GC pauses block the main thread like any other task. Frequent major GCs or unusually long pauses " +
                  "point at objects surviving into old space faster than expected — pair this with web-memory-leak.",
            bullets:
            [
                "Long pause (> 200ms) → felt as a stutter — check what was live/growing right before it",
                "Many major GCs → old-space pressure — look for the same growth signal as a memory leak",
            ]);

        sink.KeyValues([
            ("Major (full) GC cycles", data.MajorCount.ToString("N0")),
            ("Minor (young-gen) GC cycles", data.MinorCount.ToString("N0")),
            ("Total GC pause time",    $"{data.TotalPauseUs / 1000.0:F0} ms"),
            ("Longest single pause",   $"{data.LongestPauseUs / 1000.0:F0} ms"),
        ]);

        if (data.Pauses.Count > 0)
            sink.Table(
                ["Kind", "Duration", "Timestamp (trace-relative)"],
                data.Pauses.Take(30).Select(p => new[]
                {
                    p.Kind,
                    $"{p.DurationUs / 1000.0:F1} ms",
                    $"{p.TimestampUs / 1000.0:F0} ms",
                }).ToList(),
                "Ranked by duration descending, top 30 shown.");

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
    }
}
