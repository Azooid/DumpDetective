using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class GcRootsReport
{
    public void Render(GcRootsData data, IRenderSink sink, bool noIndirect = false)
    {
        sink.Section($"GC Roots: {data.TypeName}");

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
