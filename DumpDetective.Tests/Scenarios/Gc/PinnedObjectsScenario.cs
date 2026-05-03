using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;
using System.Runtime.InteropServices;

namespace DumpDetective.Tests.Scenarios.Gc;

public sealed class PinnedObjectsScenario : IScenario
{
    private static readonly List<GCHandle> _handles = [];

    public string CommandName => "pinned-objects";
    public string Description => "100 pinned GCHandles anchoring byte arrays.";

    public void Setup()
    {
        for (int i = 0; i < 100; i++)
            _handles.Add(GCHandle.Alloc(new byte[1_024], GCHandleType.Pinned));
    }

    public void Validate(ReportDoc doc)
    {
        // Section always present
        DocAssert.HasSection(doc, "Pinned Objects");

        // Type breakdown: [Type, Count, Total Size, Async-Pinned, GC-Pinned]
        DocAssert.TableByHeadersHasMinRows(doc, 1, "pinned types table",
            "Type", "Count", "Total Size", "Async-Pinned", "GC-Pinned");

        // We pinned 100 byte[] arrays via GCHandleType.Pinned
        DocAssert.AnyTableContainsText(doc, "Byte[]",
            "our 100 pinned byte[] handles must appear");

        // Generation breakdown: [Generation, Count, Total Size]
        DocAssert.TableByHeadersHasMinRows(doc, 1, "generation breakdown table",
            "Generation", "Count", "Total Size");

        // GC-Pinned column must contain a value ≥ 100 (we registered 100 Pinned handles)
        DocAssert.FindTable(doc,
            rows => rows.Any(r =>
                r.Length >= 5 &&
                long.TryParse(r[4].Replace(",", ""), out long gcPinned) && gcPinned >= 100),
            "GC-Pinned column must show ≥ 100 (we registered 100 Pinned GCHandles)");
    }

    public void Teardown()
    {
        foreach (var h in _handles)
            if (h.IsAllocated) h.Free();
        _handles.Clear();
    }
}
