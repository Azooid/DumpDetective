using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class RootCauseChainReport
{
    public void Render(RootCauseChainData data, IRenderSink sink, int top = 10)
    {
        sink.Explain(
            what: "Root cause chain synthesis — combines outputs of all trace analyzers and correlation findings into ranked causal chains with actionable advice.",
            why: "Individual analyzer findings describe symptoms. Root cause chains connect those symptoms to a single underlying cause, eliminating alert noise and prioritizing remediation.",
            impact: "Fixing the root cause of a causal chain typically resolves multiple downstream symptoms simultaneously.",
            bullets: [
                "Causal chains       — ranked root-cause → effect sequences",
                "Contributing areas  — which analyzer domains contributed to each chain",
                "Actionable advice   — specific steps to address the root cause"
            ],
            action: "Start with the highest-scoring Critical chain. " +
                    "Each chain links to the relevant analyzer commands for deeper drill-down. " +
                    "After remediation, re-run the trace to verify the chain is resolved."
        );

        sink.Section("Root Cause Summary", "rootcause-summary");
        sink.KeyValues([
            ("Trace",              data.TraceInfo),
            ("Total chains",       data.TotalChains.ToString("N0")),
            ("Top severity",       data.TotalChains > 0 ? data.TopSeverity.ToString() : "None"),
        ]);

        if (!data.HasData || data.TotalChains == 0)
        {
            sink.Alert(AlertLevel.Info, "No dominant root-cause chains identified.",
                "Either all analyzer data was clean, or insufficient data was collected to derive chains.",
                "Ensure trace-analyze is run with all providers enabled for maximum signal coverage.");
            return;
        }

        int critical = data.CausalChains.Count(c => c.Severity == FindingSeverity.Critical);
        int warning  = data.CausalChains.Count(c => c.Severity == FindingSeverity.Warning);

        if (critical > 0)
            sink.Alert(AlertLevel.Critical,
                $"{critical} critical root-cause chain(s) identified.",
                "Address these issues first — they are likely causing user-visible failures.");
        else if (warning > 0)
            sink.Alert(AlertLevel.Warning,
                $"{warning} warning-level chain(s) identified.",
                "These patterns indicate degraded performance that may worsen under load.");

        sink.Section("Causal Chains (ranked by score)", "rootcause-chains");

        var rows = new List<string[]>(Math.Min(top, data.CausalChains.Count));
        foreach (var c in data.CausalChains.Take(top))
        {
            string effects    = string.Join(" → ", c.Effects);
            string areas      = string.Join(", ", c.ContributingAreas);
            rows.Add([c.Severity.ToString(), c.Score.ToString("N0"),
                      c.RootCause, effects, areas, c.Advice ?? ""]);
        }
        sink.Table(
            ["Severity", "Score", "Root Cause", "Effects", "Contributing Areas", "Advice"],
            rows, $"Top {Math.Min(top, data.CausalChains.Count)} chains ordered by score descending");
    }
}
