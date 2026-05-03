using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Exceptions;

// LeakyPublisher.DataReceived with 200 lambda subscribers.
// Test asserts: "DataReceived", subscriber count ≥ 50.

public sealed class EventAnalysisScenario : IScenario
{
    private static readonly LeakyPublisher          _publisher = new();
    private static readonly List<SubscriberContext> _contexts  = [];
    public string CommandName => "event-analysis";
    public string Description => "200 lambda subscribers attached to a static event (never unsubscribed).";

    public void Setup()
    {
        for (int i = 0; i < 200; i++)
        {
            var ctx = new SubscriberContext(i, new byte[128]);
            _contexts.Add(ctx);
            _publisher.DataReceived += (_, _) => GC.KeepAlive(ctx);
        }
    }

    public void Validate(ReportDoc doc)
    {
        DocAssert.HasSection(doc, "Event Handler Leaks");
        DocAssert.AnyTableContainsText(doc, "DataReceived",
            "our LeakyPublisher.DataReceived event field (200 subscribers) must appear");
        DocAssert.FindTable(doc,
            rows => rows.Any(r =>
                r.Any(cell => long.TryParse(cell.Replace(",", ""), out long v) && v >= 50)),
            "event field with ≥ 50 subscriber count (we planted 200)");
    }

    private sealed class LeakyPublisher
    {
        public event EventHandler? DataReceived;
        public void Fire() => DataReceived?.Invoke(this, EventArgs.Empty);
    }

    private sealed class SubscriberContext(int Id, byte[] Payload)
    {
        public int    Id      = Id;
        public byte[] Payload = Payload;
    }
}
