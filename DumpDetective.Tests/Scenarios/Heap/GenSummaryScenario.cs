using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;

namespace DumpDetective.Tests.Scenarios.Heap;

file sealed record GenObject(int Id, string Label);

public sealed class GenSummaryScenario : IScenario
{
    private static readonly List<object> _objects = [];

    public string CommandName => "gen-summary";
    public string Description => "Gen2-promoted objects + LOH arrays for generation distribution.";

    public void Setup()
    {
        // 2 000 objects promoted to Gen2
        for (int i = 0; i < 2_000; i++)
            _objects.Add(new GenObject(i, $"gen-item-{i}"));

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);

        // 300 more in Gen0/Gen1 at dump time
        for (int i = 0; i < 300; i++)
            _objects.Add(new GenObject(i + 10_000, $"fresh-{i}"));

        // 3 LOH arrays (> 85 KB)
        for (int i = 0; i < 3; i++)
            _objects.Add(new byte[100_000]);
    }

    public void Validate(ReportDoc doc)
    {
        // Section must be present
        DocAssert.HasSection(doc, "Generation Size Breakdown");

        // All five GC regions must appear as KV rows
        DocAssert.HasKeyValue(doc, "Gen0");
        DocAssert.HasKeyValue(doc, "Gen1");
        DocAssert.HasKeyValue(doc, "Gen2");
        DocAssert.HasKeyValue(doc, "LOH");   // we planted 3 × 100 KB LOH arrays
        DocAssert.HasKeyValue(doc, "Total");

        // Segment details table must exist (collapsible, but rows are present)
        DocAssert.TableHasMinRows(doc, 1, "segment details table");
    }
}
