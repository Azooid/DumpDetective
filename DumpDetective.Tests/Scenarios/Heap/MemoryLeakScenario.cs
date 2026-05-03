using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;

namespace DumpDetective.Tests.Scenarios.Heap;

public sealed class MemoryLeakScenario : IScenario
{
    // Static list that is never cleared — simulates a real leak accumulation
    private static readonly List<byte[]> _leaked = [];

    public string CommandName => "memory-leak";
    public string Description => "~5 MB of byte arrays held in a static list (never cleared).";

    public void Setup()
    {
        // 50 × 100 KB = 5 MB
        for (int i = 0; i < 50; i++)
            _leaked.Add(new byte[100_000]);
    }

    public void Validate(ReportDoc doc)
    {
        // Always-present snapshot section
        DocAssert.HasSection(doc, "Heap Snapshot");

        // Step 1 dumpheap-stat table: [Method Table, Count, Total Size, Class Name]
        DocAssert.TableByHeadersHasMinRows(doc, 10, "dumpheap-stat table",
            "Method Table", "Count", "Total Size", "Class Name");

        // We leaked 50 × 100 KB = 5 MB of byte[]; must appear in the stat table
        DocAssert.AnyTableContainsText(doc, "Byte[]",
            "our leaked byte[] arrays must appear in the heap snapshot");

        // Suspect-types analysis section must be rendered
        DocAssert.HasSection(doc, "Suspect Types");
    }
}
