using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;

namespace DumpDetective.Tests.Scenarios.Heap;

public sealed class HighRefsScenario : IScenario
{
    private sealed class HubObject(string Name, int Id)
    {
        public string Name { get; } = Name;
        public int Id { get; } = Id;
    }

    private sealed class SpokeObject(int Index, HubObject Hub)
    {
        public int Index { get; } = Index;
        public HubObject Hub { get; } = Hub;
    }

    private static HubObject? _hub;
    private static readonly List<SpokeObject> _spokes = [];

    public string CommandName => "high-refs";
    public string Description => "Hub object with 2 000 inbound references (spokes).";

    public void Setup()
    {
        _hub = new HubObject("test-hub", 1);
        for (int i = 0; i < 2_000; i++)
            _spokes.Add(new SpokeObject(i, _hub));
    }

    public void Validate(ReportDoc doc)
    {
        // Summary section always rendered
        DocAssert.HasSection(doc, "Summary");

        // Main table includes an "Inbound Refs" column
        DocAssert.TableByHeadersHasMinRows(doc, 1, "highly-referenced objects table",
            "Type", "Inbound Refs");

        // We wired 2 000 spokes → hub; the hub must appear with Inbound Refs ≥ 100
        // Column layout: Address?, Type, Inbound Refs (index 2 or 1 depending on --addresses)
        DocAssert.FindTable(doc,
            rows => rows.Any(r =>
                r.Any(cell => long.TryParse(cell.Replace(",", ""), out long v) && v >= 100)),
            "hub object with ≥ 100 inbound references (we planted 2 000)");
    }
}
