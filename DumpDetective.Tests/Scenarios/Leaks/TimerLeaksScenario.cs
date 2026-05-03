using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;

namespace DumpDetective.Tests.Scenarios.Leaks;

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
                callback: static s => GC.KeepAlive(s),
                state:    $"dd-timer-{id:D3}",
                dueTime:  Timeout.Infinite,
                period:   Timeout.Infinite));
        }
    }

    public void Validate(ReportDoc doc)
    {
        // Summary section always rendered
        DocAssert.HasSection(doc, "Summary");

        // Timer callback table: [Callback Method, Module (DLL), Count, Period, Due In]
        // We planted 200 timers — at least 1 group row must appear
        DocAssert.TableByHeadersHasMinRows(doc, 1, "timer callback table",
            "Callback Method", "Module (DLL)", "Count", "Period", "Due In");

        // Verify the callback table shows Count ≥ 200 (all 200 timers share the same callback)
        // Count is at column index 2 in [Callback Method, Module (DLL), Count, Period, Due In]
        DocAssert.FindTable(doc,
            rows => rows.Any(r =>
                r.Length > 2 &&
                long.TryParse(r[2].Replace(",", ""), out long v) && v >= 200),
            "timer callback table must show Count ≥ 200 (planted 200 timers with same callback)");

        // Alert must mention the timer count; we planted 200 so ≥ 100 is a safe minimum
        DocAssert.AlertContains(doc, "timer");

        // Period distribution section must be present
        DocAssert.HasSection(doc, "Timer Period Distribution");
    }

    public void Teardown()
    {
        foreach (var t in _timers) t.Dispose();
        _timers.Clear();
    }
}
