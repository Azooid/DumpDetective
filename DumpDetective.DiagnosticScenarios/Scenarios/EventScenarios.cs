namespace DumpDetective.DiagnosticScenarios.Scenarios;

// Scenario for: event-analysis
// Creates a static event publisher with 500 anonymous lambda subscribers
// that are never unsubscribed. DumpDetective event-analysis walks delegate
// fields on heap objects, counts subscribers, and flags static publishers
// where subscribers are accumulating.
internal static class EventScenarios
{
    // Static publisher — its event field is a root, so every subscriber closure
    // is kept alive transitively. This is the most common real-world event leak.
    private static readonly LeakyPublisher _publisher = new();
    private static int _subscriberCount;

    public static IResult TriggerEventAnalysis()
    {
        const int count = 500;
        for (int i = 0; i < count; i++)
        {
            // Each lambda captures 'i' and 'payload', creating a distinct closure object.
            var payload = new SubscriberPayload(i, $"subscriber-context-{i}", new byte[256]);
            _publisher.DataReceived += (sender, args) =>
            {
                // Closure over payload — keeps it alive as long as the event is subscribed.
                GC.KeepAlive(payload);
                _ = args.Value + i;
            };
        }
        _subscriberCount += count;

        return Results.Ok(new
        {
            message = $"{_subscriberCount} lambda subscribers attached to static event; none ever unsubscribed.",
            command = "DumpDetective event-analysis <dump.dmp>",
            hint = "Each subscriber closure captures a SubscriberPayload with a 256-byte array.",
        });
    }

    public static string Status => $"event-analysis: {_subscriberCount} subscribers on static event";

    public static void Reset()
    {
        // Cannot cleanly remove anonymous lambdas; mark count as reset
        _subscriberCount = 0;
    }

    // ── Supporting types ──────────────────────────────────────────────────────

    private sealed class DataArgs(int value) : EventArgs { public int Value = value; }

    private sealed class LeakyPublisher
    {
        public event EventHandler<DataArgs>? DataReceived;

        // Called periodically so the event handler chain is exercised, keeping
        // closures warm in the GC root graph.
        public void Raise(int v) => DataReceived?.Invoke(this, new DataArgs(v));
    }

    private sealed class SubscriberPayload(int id, string context, byte[] buffer)
    {
        public int    Id      = id;
        public string Context = context;
        public byte[] Buffer  = buffer;
    }
}
