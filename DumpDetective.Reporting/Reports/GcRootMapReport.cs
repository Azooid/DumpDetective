using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class GcRootMapReport
{
    public void Render(GcRootMapData data, IRenderSink sink)
    {
        sink.Section("GC Root Classification");
        sink.Explain(
            what: "All GC roots classified by kind — Strong handles, Pinned handles, Async-pinned, Weak handles, " +
                  "RefCounted (COM), Dependent, SizedRef, and thread stack roots.",
            why:  "A GC root is any reference the garbage collector treats as permanently live. " +
                  "Understanding the distribution of root kinds reveals why objects cannot be collected. " +
                  "A high number of Strong handles suggests a static cache or global registry retaining objects. " +
                  "Many Pinned handles cause heap fragmentation. Async-pinned handles are created by overlapped I/O.",
            bullets:
            [
                "Strong → explicit GCHandle.Alloc(obj) or static fields — most common leak source",
                "Pinned → IOCompletion / Marshal.AllocHGlobal patterns — prevent heap compaction",
                "AsyncPinned → overlapped I/O buffers — normally transient; high count = I/O bottleneck",
                "WeakShort/WeakLong → weak references; target may still be alive but not preventing collection",
                "ThreadStack → objects referenced by local variables and method arguments on live thread stacks",
            ],
            impact: "Objects reachable from Strong or ThreadStack roots cannot be collected. " +
                    "The larger the memory held by these root kinds, the more memory is pinned in the process.");

        sink.KeyValues([
            ("Total GC handles",    data.TotalHandles.ToString("N0")),
            ("Thread stack roots",  (data.StackRootsPartial
                                        ? data.TotalStackRoots.ToString("N0") + "  (partial — 10 s budget)"
                                        : data.TotalStackRoots.ToString("N0"))),
            ("Total handle memory", DumpHelpers.FormatSize(data.TotalHandleMemory)),
        ]);

        // Root kind breakdown donut
        var donutSegs = data.ByKind
            .Where(k => k.HandleCount + k.StackRootCount > 0)
            .Select(k => (Label: k.KindName, Value: (double)(k.HandleCount + k.StackRootCount)))
            .ToList();
        if (donutSegs.Count > 1)
            sink.DonutChart(donutSegs, "Roots by kind", $"{data.TotalHandles + data.TotalStackRoots:N0}\ntotal");

        // Handle kinds table
        var kindRows = data.ByKind
            .OrderByDescending(k => k.EstimatedMemory)
            .Select(k => new[]
            {
                k.KindName,
                k.HandleCount > 0 ? k.HandleCount.ToString("N0") : "—",
                k.StackRootCount > 0 ? k.StackRootCount.ToString("N0") : "—",
                k.EstimatedMemory > 0 ? DumpHelpers.FormatSize(k.EstimatedMemory) : "—",
            })
            .ToList();
        sink.Table(["Root Kind", "Handles", "Stack Roots", "Est. Memory"], kindRows,
            "Estimated memory = sum of target object sizes per root kind (own size only, not retained graph).");

        // Top types per kind
        sink.Section("Top Types by Root Kind");
        sink.Explain(
            what: "The most frequently rooted object types per GC root category.",
            why:  "A specific type appearing thousands of times in Strong handles indicates a registration " +
                  "or cache pattern that prevents collection. Types in ThreadStack are normal for running code.");

        var grouped = data.TopTypesByKind
            .GroupBy(r => r.KindName)
            .OrderByDescending(g => g.Sum(r => r.TotalSize));

        foreach (var grp in grouped)
        {
            sink.BeginDetails($"{grp.Key} — top types", open: grp.Key is "Strong" or "Pinned");
            sink.Table(
                ["Type", "Count", "Total Size"],
                grp.Select(r => new[]
                {
                    r.TypeName.Length > 70 ? r.TypeName[..70] + "…" : r.TypeName,
                    r.Count.ToString("N0"),
                    DumpHelpers.FormatSize(r.TotalSize),
                }).ToList());
            sink.EndDetails();
        }
    }
}
