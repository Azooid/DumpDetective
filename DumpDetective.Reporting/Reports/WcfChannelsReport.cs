using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class WcfChannelsReport
{
    public void Render(WcfChannelsData data, IRenderSink sink, bool showAddr = false)
    {
        sink.Section("Summary");
        if (data.Objects.Count == 0) { sink.Text("No WCF objects found."); return; }

        int faultedTotal = data.Objects.Count(o => o.State == "Faulted");
        int openedTotal  = data.Objects.Count(o => o.State == "Opened");

        sink.KeyValues([
            ("Total WCF objects",    data.Objects.Count.ToString("N0")),
            ("Opened",               openedTotal.ToString("N0")),
            ("Faulted",              faultedTotal.ToString("N0")),
            ("Distinct endpoints",   data.Objects.Select(o => o.Endpoint).Where(e => e.Length > 0).Distinct().Count().ToString("N0")),
        ]);

        if (faultedTotal > 0)
            sink.Alert(AlertLevel.Critical, $"{faultedTotal} faulted WCF channel(s) found.",
                "Faulted channels cannot be reused and must be aborted before creating new ones.",
                "Call IChannel.Abort() (not Close()) on faulted channels.");

        RenderSummaryTable(sink, data);
        RenderEndpoints(sink, data);
        RenderFaultReasons(sink, data);
        if (showAddr) RenderAddresses(sink, data);
    }

    private static void RenderSummaryTable(IRenderSink sink, WcfChannelsData data)
    {
        var grouped = data.Objects.GroupBy(o => o.Type).OrderByDescending(g => g.Count()).ToList();
        var rows = grouped.Select(g =>
        {
            int faulted = g.Count(o => o.State == "Faulted");
            int opened  = g.Count(o => o.State == "Opened");
            string bindings = string.Join(", ", g.Select(o => o.Binding).Where(b => b.Length > 0).Distinct().Take(3));
            return new[] { g.Key, g.Count().ToString("N0"), opened.ToString("N0"),
                faulted > 0 ? faulted.ToString("N0") : "—", bindings.Length > 0 ? bindings : "—" };
        }).ToList();
        sink.Table(["Type", "Count", "Opened", "Faulted", "Binding"], rows);
    }

    private static void RenderEndpoints(IRenderSink sink, WcfChannelsData data)
    {
        sink.Section("Endpoints");
        var epRows = data.Objects
            .Where(o => o.Endpoint.Length > 0)
            .GroupBy(o => o.Endpoint)
            .Select(g => new[] { g.Key, g.Count().ToString("N0"),
                g.Count(o => o.State == "Faulted").ToString("N0") })
            .OrderByDescending(r => int.Parse(r[1].Replace(",", "")))
            .ToList();
        if (epRows.Count > 0)
            sink.Table(["Endpoint", "Objects", "Faulted"], epRows);
        else
            sink.Text("No endpoint addresses resolved.");
    }

    private static void RenderFaultReasons(IRenderSink sink, WcfChannelsData data)
    {
        var faultRows = data.Objects
            .Where(o => o.FaultReason.Length > 0)
            .GroupBy(o => o.FaultReason)
            .Select(g => new[] { g.Key, g.Count().ToString("N0") })
            .OrderByDescending(r => int.Parse(r[1].Replace(",", "")))
            .ToList();
        if (faultRows.Count > 0)
        {
            sink.Section("Fault Reasons");
            sink.Table(["Fault Reason", "Count"], faultRows);
        }
    }

    private static void RenderAddresses(IRenderSink sink, WcfChannelsData data)
    {
        sink.Section("Object Addresses");
        var rows = data.Objects.Select(o => new[]
            { o.Type, $"0x{o.Addr:X16}", o.State, o.Endpoint, o.FaultReason }).ToList();
        sink.Table(["Type", "Address", "State", "Endpoint", "Fault Reason"], rows);
    }
}
