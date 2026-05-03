using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Heap;

// ~5 MB of byte arrays in a static list.

public sealed class MemoryLeakScenario : IScenario
{
    private static readonly List<byte[]> _leaked = [];
    public string CommandName => "memory-leak";
    public string Description => "~5 MB of byte arrays held in a static list (never cleared).";

    public void Setup()
    {
        for (int i = 0; i < 50; i++)
            _leaked.Add(new byte[100_000]);
    }

    public void Validate(ReportDoc doc)
    {
        DocAssert.HasSection(doc, "Heap Snapshot");
        DocAssert.TableByHeadersHasMinRows(doc, 10, "dumpheap-stat table",
            "Method Table", "Count", "Total Size", "Class Name");
        DocAssert.AnyTableContainsText(doc, "Byte[]",
            "our leaked byte[] arrays must appear in the heap snapshot");
        DocAssert.HasSection(doc, "Suspect Types");
    }
}
