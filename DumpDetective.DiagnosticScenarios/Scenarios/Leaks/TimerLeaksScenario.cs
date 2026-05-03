using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Leaks;

// 200 System.Threading.Timer instances never disposed.

public sealed class TimerLeaksScenario : IScenario
{
    private static readonly List<Timer> _timers = [];
    public string CommandName => "timer-leaks";
    public string Description => "200 System.Threading.Timer instances never disposed.";

    public void Setup()
    {
        for (int i = 0; i < 200; i++)
        {
            int id = i;
            _timers.Add(new Timer(
                static s => GC.KeepAlive(s),
                $"dd-timer-{id:D3}",
                Timeout.Infinite, Timeout.Infinite));
        }
    }

    public void Validate(ReportDoc doc)
    {
        DocAssert.HasSection(doc, "Summary");
        DocAssert.TableByHeadersHasMinRows(doc, 1, "timer callback table",
            "Callback Method", "Module (DLL)", "Count", "Period", "Due In");
        DocAssert.FindTable(doc,
            rows => rows.Any(r =>
                r.Length > 2 &&
                long.TryParse(r[2].Replace(",", ""), out long v) && v >= 200),
            "timer callback table must show Count ≥ 200 (planted 200 timers with same callback)");
        DocAssert.AlertContains(doc, "timer");
        DocAssert.HasSection(doc, "Timer Period Distribution");
    }

    public void Teardown()
    {
        foreach (var t in _timers) t.Dispose();
        _timers.Clear();
    }
}
