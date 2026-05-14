using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class AnomalyDetectionReport
{
    public void Render(AnomalyDetectionData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "Statistical anomaly detection using rolling z-scores over metric timelines already computed by other trace analyzers — no additional trace parsing required.",
            why: "Individual metric thresholds (e.g. GC pause > 200 ms) produce false positives under sustained load. Z-scores detect anomalies relative to the trace's own baseline.",
            impact: "A z-score > 3 means the observed value is more than 3 standard deviations above the rolling mean — statistically unusual given recent history.",
            bullets: [
                "Metric anomalies    — data points more than 3σ above the rolling baseline",
                "Z-score             — how many standard deviations above the mean",
                "Severity thresholds — Info (z≥3), Warning (z≥4), Critical (z≥6)"
            ],
            action: "Correlate anomaly timestamps with deployment events, traffic spikes, or dependency failures. " +
                    "Use 'root-cause-trace' to see synthesized causal chains that include these anomalies."
        );

        sink.Section("Trace Summary", "anomaly-summary");
        sink.KeyValues([
            ("Trace",              data.TraceInfo),
            ("Total anomalies",    data.TotalAnomalies.ToString("N0")),
            ("Baseline window",    $"{data.BaselineWindowSec} seconds"),
            ("Z-score threshold",  "3.0"),
            ("Process filter",     data.FilteredProcess ?? "(all processes)"),
        ]);

        if (!data.HasData || data.TotalAnomalies == 0)
        {
            sink.Alert(AlertLevel.Info, "No statistical anomalies detected.",
                "All metric timelines were within 3σ of their rolling baseline for the configured window.",
                "Ensure other analyzers (gc-trace, alloc-burst-trace, etc.) are included in the same trace-analyze run.");
            return;
        }

        int critical = data.Anomalies.Count(a => a.Severity == FindingSeverity.Critical);
        int warning  = data.Anomalies.Count(a => a.Severity == FindingSeverity.Warning);

        if (critical > 0)
            sink.Alert(AlertLevel.Critical,
                $"{critical} critical anomaly(ies) detected (z ≥ 6).",
                "Extreme deviations from baseline — investigate immediately.");
        else if (warning > 0)
            sink.Alert(AlertLevel.Warning,
                $"{warning} anomaly(ies) at warning level (z ≥ 4).",
                "Significant deviations above baseline — correlate with other signals.");

        sink.Section("Detected Anomalies", "anomaly-list");
        var rows = new List<string[]>(Math.Min(top, data.Anomalies.Count));
        foreach (var a in data.Anomalies.Take(top))
            rows.Add([a.MetricName, $"{a.TimeMs / 1000:F1} s",
                       $"{a.ObservedValue:F1}", $"{a.BaselineValue:F1}",
                       $"{a.ZScore:F1}", a.Severity.ToString(), a.Description]);
        sink.Table(
            ["Metric", "Time", "Observed", "Baseline", "Z-Score", "Severity", "Description"],
            rows, "Anomalies ordered by z-score descending");
    }
}
