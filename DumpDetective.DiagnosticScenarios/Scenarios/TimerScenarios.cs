namespace DumpDetective.DiagnosticScenarios.Scenarios;

// Scenario for: timer-leaks
// Creates 300 System.Threading.Timer instances that are never disposed.
// DumpDetective timer-leaks walks the heap for Timer objects and reports
// their count, callback method names, and estimated memory impact.
internal static class TimerScenarios
{
    // Static list holds the timers alive so the GC cannot collect them.
    // (A Timer whose handle has been GC'd will silently stop firing.)
    private static readonly List<Timer> _timers = [];

    public static IResult TriggerTimerLeaks()
    {
        const int count = 300;
        for (int i = 0; i < count; i++)
        {
            int id = i;
            // Long period so the callback overhead is negligible during the scenario
            var timer = new Timer(TimerCallback, state: $"timer-{id:D3}", dueTime: Timeout.Infinite, period: Timeout.Infinite);
            _timers.Add(timer);
        }

        return Results.Ok(new
        {
            message = $"{_timers.Count} System.Threading.Timer instances on heap (never disposed).",
            command = "DumpDetective timer-leaks <dump.dmp>",
            hint = "Each timer captures a closure over its 'id' — look for TimerCallback state objects too.",
        });
    }

    public static string Status => $"timer-leaks: {_timers.Count} Timer objects";

    public static void Reset()
    {
        foreach (var t in _timers) t.Dispose();
        _timers.Clear();
    }

    private static void TimerCallback(object? state)
    {
        // Intentionally empty — the scenario only needs the Timer objects on the heap
        GC.KeepAlive(state);
    }
}
