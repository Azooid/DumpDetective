using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;

namespace DumpDetective.Tests.Scenarios.Heap;

public sealed class LargeObjectsScenario : IScenario
{
    // Static — survives across GC cycles and stays in the dump
    private static readonly List<byte[]> _lohArrays = [];

    public string CommandName => "large-objects";
    public string Description => "20 × 200 KB byte arrays on the LOH.";

    public void Setup()
    {
        for (int i = 0; i < 20; i++)
            _lohArrays.Add(new byte[200_000]); // 200 KB > LOH threshold of 85 KB
    }

    public void Validate(ReportDoc doc)
    {
        // Type aggregate section always rendered
        DocAssert.HasSection(doc, "Type Aggregate");

        // Type aggregate table: [Type, Element Type, Count, Total Size, % of LOH+]
        DocAssert.TableByHeadersHasMinRows(doc, 1, "LOH type aggregate table",
            "Type", "Element Type", "Count", "Total Size");

        // We planted 20 × 200 KB byte[]; byte[] must appear in the aggregate table
        DocAssert.AnyTableContainsText(doc, "Byte[]",
            "System.Byte[] (our 20 × 200 KB LOH arrays) must appear");

        // Individual objects section should list our large arrays
        DocAssert.HasSection(doc, "Largest Individual Objects");
    }
}
