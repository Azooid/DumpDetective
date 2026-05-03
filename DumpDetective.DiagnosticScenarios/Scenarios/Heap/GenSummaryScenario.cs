using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Heap;

// Objects promoted to Gen2 + fresh Gen0 + LOH.

file sealed record GenObject(int Id, string Label);

public sealed class GenSummaryScenario : IScenario
{
    private static readonly List<object> _objects = [];
    public string CommandName => "gen-summary";
    public string Description => "Gen2-promoted objects + LOH arrays for generation distribution.";

    public void Setup()
    {
        for (int i = 0; i < 2_000; i++) _objects.Add(new GenObject(i, $"gen-item-{i}"));
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        for (int i = 0; i < 300; i++) _objects.Add(new GenObject(i + 10_000, $"fresh-{i}"));
        for (int i = 0; i < 3; i++) _objects.Add(new byte[100_000]); // LOH
    }

    public void Validate(ReportDoc doc)
    {
        DocAssert.HasSection(doc, "Generation Size Breakdown");
        DocAssert.HasKeyValue(doc, "Gen0");
        DocAssert.HasKeyValue(doc, "Gen1");
        DocAssert.HasKeyValue(doc, "Gen2");
        DocAssert.HasKeyValue(doc, "LOH");
        DocAssert.HasKeyValue(doc, "Total");
        DocAssert.TableHasMinRows(doc, 1, "segment details table");
    }
}
