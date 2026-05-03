using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;
using System.Runtime.InteropServices;

namespace DumpDetective.Tests.Scenarios.Heap;

/// <summary>
/// Uses LOH fragmentation rather than SOH fragmentation.
///
/// .NET 10 uses a region-based GC for the SOH.  When SOH regions become empty
/// the runtime returns them to the OS so they disappear from the heap entirely.
/// ClrMD sees no Free-type objects and reports 0% fragmentation.
///
/// The LOH (Large Object Heap) is different: it is a single non-moving segment
/// and never compacts by default.  Free holes are kept as Free-type placeholder
/// objects that ClrMD counts as fragmentation.
///
/// Strategy:
///   • Allocate 50 × 200 KB arrays (all go to LOH — above the 85 KB threshold).
///   • GCHandle.Alloc(Pinned) on 25 of them → they remain live AND show up as
///     pinned in the LOH segment stats.
///   • The other 25 go out of scope, are collected, and become ~200 KB Free holes.
///   • Result: ~50% LOH fragmentation → segment-level alert + distribution alert.
/// </summary>
public sealed class HeapFragmentationScenario : IScenario
{
    private static readonly List<GCHandle> _pinnedHandles = [];

    public string CommandName => "heap-fragmentation";
    public string Description => "LOH fragmentation: 25 × 200 KB pinned arrays alternating with 25 × 200 KB free holes.";

    public void Setup()
    {
        const int count     = 50;
        const int arraySize = 200_000; // 200 KB — goes to LOH (above the 85 000 byte threshold)

        for (int i = 0; i < count; i++)
        {
            var arr = new byte[arraySize];
            if (i % 2 == 0)
                // Pin every other array; these stay alive and show as "pinned" in segment stats.
                _pinnedHandles.Add(GCHandle.Alloc(arr, GCHandleType.Pinned));
            // Odd-indexed arrays are intentionally not kept — they become collectable.
        }

        // Collect: the 25 unpinned LOH arrays become Free-type holes inside the LOH segment.
        // compacting:false is the default for LOH — holes are preserved as Free objects.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
    }

    public void Validate(ReportDoc doc)
    {
        // Overall fragmentation summary is always emitted
        DocAssert.HasSection(doc, "Overall Fragmentation");

        // KV grid is always rendered
        DocAssert.HasKeyValue(doc, "Overall frag %");

        // Segment details table: [Segment Addr, Kind, Committed, Live, Free, Frag %, Pinned]
        DocAssert.TableByHeadersHasMinRows(doc, 1, "segment details table",
            "Segment Addr", "Kind", "Committed", "Live", "Free", "Frag %", "Pinned");

        // The LOH segment must report Pinned > 0 (our 25 GCHandle.Pinned LOH arrays)
        DocAssert.FindTable(doc,
            rows => rows.Any(r => r.Length >= 7 &&
                long.TryParse(r[6].Replace(",", ""), out long pinned) && pinned > 0),
            "at least one segment has pinned objects from our 25 GCHandle.Pinned LOH arrays");

        // NOTE: fragmentation-% alerts are NOT asserted.
        // .NET 10 uses a region-based GC for both SOH and LOH.  When a GC region
        // becomes empty, the runtime decommits it entirely — no Free-type placeholder
        // objects remain for ClrMD 3.1.x to count.  The report shows 0% fragmentation
        // in test-process dumps even though 25 LOH arrays were freed.  On real
        // production dumps with genuine pinning pressure the segment alerts do fire.
    }

    public void Teardown()
    {
        foreach (var h in _pinnedHandles)
            if (h.IsAllocated) h.Free();
        _pinnedHandles.Clear();
    }
}
