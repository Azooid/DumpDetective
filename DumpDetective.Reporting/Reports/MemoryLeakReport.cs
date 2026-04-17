using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class MemoryLeakReport
{
    public void Render(MemoryLeakData data, IRenderSink sink, int top = 30, bool includeSystem = false)
    {
        sink.Section("Step 1 — Heap Type Statistics");
        sink.KeyValues([
            ("Total heap size",          DumpHelpers.FormatSize(data.TotalHeapSize)),
            ("Distinct types",           data.AllTypes.Count.ToString("N0")),
            ("System.String total size", DumpHelpers.FormatSize(data.TotalStringSize)),
            ("System.String count",      data.TotalStringCount.ToString("N0")),
        ]);

        var allRows = data.AllTypes.Take(top).Select(r => new[]
        {
            r.Name, r.Count.ToString("N0"), DumpHelpers.FormatSize(r.Size), r.Gen,
        }).ToList();
        sink.Table(["Type", "Count", "Total Size", "Gen"], allRows,
            $"Top {allRows.Count} types by total size");

        sink.Section("Step 2 — Application Type Suspects");
        if (data.AppSuspects.Count == 0)
        {
            sink.Text("No application-type suspects found with the current threshold.");
        }
        else
        {
            var suspectRows = data.AppSuspects.Select(r => new[]
            {
                r.Name, r.Count.ToString("N0"), DumpHelpers.FormatSize(r.Size), r.Gen,
            }).ToList();
            sink.Table(["Type", "Count", "Total Size", "Gen"], suspectRows,
                "Non-system types  |  sorted by total size");
        }

        sink.Section("Step 3 — String Accumulation Check");
        if (data.TotalStringSize > 100 * 1024 * 1024)
            sink.Alert(AlertLevel.Warning, $"{DumpHelpers.FormatSize(data.TotalStringSize)} in {data.TotalStringCount:N0} System.String objects.",
                "String accumulation is a symptom — strings stay alive because parent objects (caches, collections) are not evicted.",
                "Run string-duplicates to see which values dominate. Find the root collection holding them with static-refs.");
        else if (data.TotalStringSize > 10 * 1024 * 1024)
            sink.Alert(AlertLevel.Info, $"{DumpHelpers.FormatSize(data.TotalStringSize)} in System.String objects — run string-duplicates for detail.");
        else
            sink.Text($"String pressure is low: {DumpHelpers.FormatSize(data.TotalStringSize)} in {data.TotalStringCount:N0} instances.");

        if (data.RootChains.Count > 0)
            RenderRootChains(sink, data);
        else
            sink.Section("Step 4 — GC Root Chains");
    }

    private static void RenderRootChains(IRenderSink sink, MemoryLeakData data)
    {
        sink.Section("Step 4 — GC Root Chains");
        foreach (var rc in data.RootChains)
        {
            sink.BeginDetails(
                $"{rc.TypeName}  (sample @ 0x{rc.SampleAddr:X16})",
                open: false);

            if (rc.Chain.Count > 0)
                sink.Table(["Root Chain (root → ... → object)"],
                    rc.Chain.Select(c => new[] { c }).ToList());
            else
                sink.Text("  (root not found within budget — increase heap walk)");

            sink.EndDetails();
        }
    }
}
