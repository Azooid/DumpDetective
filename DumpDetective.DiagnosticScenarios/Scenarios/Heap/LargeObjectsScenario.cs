using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Heap;

// 20 × 200 KB byte arrays on the LOH.

public sealed class LargeObjectsScenario : IScenario
{
    private static readonly List<byte[]> _arrays = [];
    public string CommandName => "large-objects";
    public string Description => "20 × 200 KB byte arrays on the LOH.";

    public void Setup()
    {
        for (int i = 0; i < 20; i++)
            _arrays.Add(new byte[200_000]);
    }

    public void Validate(ReportDoc doc)
    {
        DocAssert.HasSection(doc, "Type Aggregate");
        DocAssert.TableByHeadersHasMinRows(doc, 1, "LOH type aggregate table",
            "Type", "Element Type", "Count", "Total Size");
        DocAssert.AnyTableContainsText(doc, "Byte[]",
            "System.Byte[] (our 20 × 200 KB LOH arrays) must appear");
        DocAssert.HasSection(doc, "Largest Individual Objects");
    }
}
