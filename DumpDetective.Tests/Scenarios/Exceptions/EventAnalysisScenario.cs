using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;

namespace DumpDetective.Tests.Scenarios.Exceptions;

public sealed class EventAnalysisScenario : IScenario
{
    private static readonly LeakyPublisher _publisher = new();
    private static readonly List<SubscriberContext> _contexts = [];

    public string CommandName => "event-analysis";
    public string Description => "200 lambda subscribers attached to a static event (never unsubscribed).";

    public void Setup()
    {
        const int count = 200;
        for (int i = 0; i < count; i++)
        {
            var ctx = new SubscriberContext(i, new byte[128]);
            _contexts.Add(ctx);
            _publisher.DataReceived += (_, _) => GC.KeepAlive(ctx);
        }
    }

    public void Validate(ReportDoc doc)
    {
        // Section "1. Event Handler Leaks" is always emitted
        DocAssert.HasSection(doc, "Event Handler Leaks");

        // The event summary table must contain our planted event field
        // LeakyPublisher.DataReceived had 200 subscribers attached
        DocAssert.AnyTableContainsText(doc, "DataReceived",
            "our LeakyPublisher.DataReceived event field (200 subscribers) must appear");

        // With 200 subscribers, some subscriber count must be ≥ 50 in any table cell
        DocAssert.FindTable(doc,
            rows => rows.Any(r =>
                r.Any(cell => long.TryParse(cell.Replace(",", ""), out long v) && v >= 50)),
            "event field with ≥ 50 subscriber count (we planted 200)");
    }

    // ── Supporting types ──────────────────────────────────────────────────────
    private sealed class LeakyPublisher
    {
        public event EventHandler? DataReceived;
        public void Fire() => DataReceived?.Invoke(this, EventArgs.Empty);
    }

    private sealed class SubscriberContext(int Id, byte[] Payload)
    {
        public int    Id      { get; } = Id;
        public byte[] Payload { get; } = Payload;
    }
}
