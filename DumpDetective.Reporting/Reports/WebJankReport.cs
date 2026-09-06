using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class WebJankReport
{
    public void Render(WebJankData data, IRenderSink sink)
    {
        sink.Section("Jank / Dropped Frames");
        sink.Explain(
            what: "The compositor's frame drop rate — every BeginFrame is one attempted frame; a DroppedFrame " +
                  "is one that was scheduled but never made it to the screen in time.",
            why:  "Dropped frames are what a user perceives as stutter or jank, independent of raw CPU cost — " +
                  "even a fast page can drop frames if the main thread is busy at the wrong moment.",
            bullets:
            [
                "Drop rate > 10% → noticeable stutter",
                "Drop rate > 25% → the page feels broken",
            ],
            action: "Cross-reference dropped-frame timestamps with web-long-tasks / web-cpu-hotspots to find the cause.");

        sink.Gauges([("Drop rate", data.DropRatePct, "%")], barMax: 100);
        sink.KeyValues([
            ("Frames attempted (BeginFrame)", data.BeginFrameCount.ToString("N0")),
            ("Frames dropped",                data.DroppedFrameCount.ToString("N0")),
            ("Effective FPS",                 $"{data.EffectiveFps:F1}"),
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
    }
}
