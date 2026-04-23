using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class GcRootsReport
{
    public void Render(GcRootsData data, IRenderSink sink, bool noIndirect = false)
    {
        sink.Section($"GC Roots: {data.TypeName}");
        sink.Explain(
            what: "GC root chain analysis — traces every path from a GC root (stack variable, static field, GC handle) down to instances of the requested type.",
            why: "An object stays in memory as long as at least one reachable GC root holds a reference to it, directly or through a chain of other objects.",
            impact: "Objects that appear to have been 'released' stay alive if they are still reachable from a static field, finalizer queue, or live thread.",
            bullets: ["'Root Kind: Stack' = a local variable on a running thread holds this object", "'Root Kind: Static' = a static field keeps this alive for the process lifetime", "'Root Kind: Finalizer' = object is waiting to be finalized — not yet collected", "1-hop referrers show which objects hold a reference to each instance"],
            action: "Find the outermost root (usually a static field or long-lived scope) and either scope it properly or set it to null on disposal."
        );

        if (data.Targets.Count == 0)
        {
            sink.Alert(AlertLevel.Info, $"No instances of '{data.TypeName}' found on the heap.");
            return;
        }

        long totalSize = data.Targets.Sum(t => t.Size);
        sink.KeyValues([
            ("Instances found",     $"{data.Targets.Count:N0}{(data.Capped ? " (capped)" : "")}"),
            ("Total size",          DumpHelpers.FormatSize(totalSize)),
            ("1-hop referrers",     noIndirect ? "skipped (--no-indirect)" : data.Referrers.Values.Sum(r => r.Count).ToString("N0")),
        ]);

        // Root kind summary
        var allRoots  = data.DirectRoots.Values.SelectMany(r => r).ToList();
        if (allRoots.Count > 0)
        {
            var kindRows = allRoots
                .GroupBy(r => r.KindLabel)
                .OrderByDescending(g => g.Count())
                .Select(g => new[] { g.Key, g.Count().ToString("N0") })
                .ToList();
            sink.Table(["Root Kind", "Count"], kindRows, "Direct GC root kinds across all target instances");
        }

        // Per-instance details
        sink.Section("Instance Details");
        foreach (var target in data.Targets)
        {
            var roots = data.DirectRoots.TryGetValue(target.Addr, out var r) ? r : (IReadOnlyList<GcRootInfo>)[];
            var refs  = data.Referrers.TryGetValue(target.Addr, out var re) ? re : (IReadOnlyList<ReferrerInfo>)[];
            bool rooted = roots.Count > 0;

            sink.BeginDetails(
                $"0x{target.Addr:X16}  {target.Type}  |  {DumpHelpers.FormatSize(target.Size)}  |  {target.Gen}" +
                (rooted ? $"  |  {roots.Count} root(s)" : "  |  no direct roots found"),
                open: rooted);

            if (roots.Count > 0)
            {
                var rootRows = roots.Select(ro => new[]
                {
                    ro.KindLabel,
                    $"0x{ro.RootAddress:X16}",
                    ro.ThreadId.HasValue ? $"Thread {ro.ThreadId}" : "—",
                }).ToList();
                sink.Table(["Root Kind", "Root Address", "Thread"], rootRows);
            }
            else
                sink.Text("  No direct GC roots found — object may be referenced transitively.");

            if (!noIndirect && refs.Count > 0)
            {
                var refRows = refs.Take(20)
                    .Select(rf => new[] { rf.Type, $"0x{rf.Addr:X16}" }).ToList();
                sink.Table(["Referrer Type", "Referrer Address"], refRows,
                    $"Top {refRows.Count} of {refs.Count} 1-hop referrers");
            }

            sink.EndDetails();
        }
    }
}
