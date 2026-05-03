using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;
using System.Runtime.InteropServices;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Heap;

// 25 pinned LOH arrays + 25 freed → Free holes inside the LOH segment.
// Test asserts: segment table present, Pinned > 0.

public sealed class HeapFragmentationScenario : IScenario
{
    private static readonly List<GCHandle> _handles = [];
    public string CommandName => "heap-fragmentation";
    public string Description => "LOH fragmentation: 25 × 200 KB pinned arrays alternating with 25 × 200 KB free holes.";

    public void Setup()
    {
        const int count     = 50;
        const int arraySize = 200_000; // LOH (> 85 KB)

        for (int i = 0; i < count; i++)
        {
            var arr = new byte[arraySize];
            if (i % 2 == 0)
                _handles.Add(GCHandle.Alloc(arr, GCHandleType.Pinned));
            // odd-indexed → become collectable
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
    }

    public void Validate(ReportDoc doc)
    {
        DocAssert.HasSection(doc, "Overall Fragmentation");
        DocAssert.HasKeyValue(doc, "Overall frag %");
        DocAssert.TableByHeadersHasMinRows(doc, 1, "segment details table",
            "Segment Addr", "Kind", "Committed", "Live", "Free", "Frag %", "Pinned");
        DocAssert.FindTable(doc,
            rows => rows.Any(r => r.Length >= 7 &&
                long.TryParse(r[6].Replace(",", ""), out long pinned) && pinned > 0),
            "at least one segment has pinned objects from our 25 GCHandle.Pinned LOH arrays");
    }

    public void Teardown()
    {
        foreach (var h in _handles)
            if (h.IsAllocated) h.Free();
        _handles.Clear();
    }
}
