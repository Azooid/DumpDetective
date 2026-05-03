using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Heap;

// Hub object with 2 000 inbound references (spokes).
// Test asserts: inbound ref count ≥ 100.

internal sealed class HubObject(string Name, int Id)
{
    public string Name = Name;
    public int    Id   = Id;
}

internal sealed class SpokeObject(int Index, HubObject Hub)
{
    public int       Index = Index;
    public HubObject Hub   = Hub;
}

public sealed class HighRefsScenario : IScenario
{
    private static HubObject?                 _hub;
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
        DocAssert.HasSection(doc, "Summary");
        DocAssert.TableByHeadersHasMinRows(doc, 1, "highly-referenced objects table",
            "Type", "Inbound Refs");
        DocAssert.FindTable(doc,
            rows => rows.Any(r =>
                r.Any(cell => long.TryParse(cell.Replace(",", ""), out long v) && v >= 100)),
            "hub object with ≥ 100 inbound references (we planted 2 000)");
    }
}
