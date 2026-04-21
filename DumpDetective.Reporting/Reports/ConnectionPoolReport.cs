using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class ConnectionPoolReport
{
    private const int DefaultMaxPool = 100;

    public void Render(ConnectionPoolData data, IRenderSink sink, bool showAddr = false)
    {
        if (data.Connections.Count == 0) { sink.Text("No database connection objects found."); return; }

        var poolGroups = BuildPoolGroups(data.Connections);

        RenderSummary(sink, data.Connections, poolGroups);
        RenderTypeStateTable(sink, data);
        RenderPoolUtilization(sink, poolGroups);
        RenderConnectionStrings(sink, data);
        if (showAddr) RenderAddresses(sink, data);
        RenderCommands(sink, data);
    }

    private static List<(string TypeKey, string ConnKey, int Active, int Total, double UtilPct)>
        BuildPoolGroups(IReadOnlyList<ConnectionInfo> connections) =>
        connections
            .GroupBy(c => (c.TypeName, c.ConnStr.Length > 0 ? c.ConnStr : "<no-connstr>"))
            .Select(g =>
            {
                int active = g.Count(c => c.State is "Open" or "Executing" or "Fetching");
                int total  = g.Count();
                return (g.Key.TypeName, g.Key.Item2, active, total, active * 100.0 / DefaultMaxPool);
            })
            .OrderByDescending(p => p.Item5)
            .ToList();

    private static void RenderSummary(IRenderSink sink,
        IReadOnlyList<ConnectionInfo> connections,
        IReadOnlyList<(string TypeKey, string ConnKey, int Active, int Total, double UtilPct)> poolGroups)
    {
        sink.Section("Summary");
        long totalSize = connections.Sum(c => c.Size);

        sink.KeyValues([
            ("Total connection objects",   connections.Count.ToString("N0")),
            ("Total size",                 DumpHelpers.FormatSize(totalSize)),
            ("Pool groups (type+connstr)", poolGroups.Count.ToString("N0")),
        ]);

        if (connections.Count > 50)
            sink.Alert(AlertLevel.Critical, $"{connections.Count:N0} DB connection objects on heap.",
                advice: "Wrap connections in 'using'. Verify connection pool MaxPoolSize. Do not store DbContext in static fields.");
        else if (connections.Count > 10)
            sink.Alert(AlertLevel.Warning, $"{connections.Count:N0} DB connection objects on heap.");
    }

    private static void RenderTypeStateTable(IRenderSink sink, ConnectionPoolData data)
    {
        var rows = data.Connections
            .GroupBy(c => (c.TypeName, c.State))
            .OrderByDescending(g => g.Count())
            .Select(g => new[]
            {
                g.Key.TypeName,
                g.Key.State.Length > 0 ? g.Key.State : "—",
                g.Count().ToString("N0"),
                DumpHelpers.FormatSize(g.Sum(c => c.Size)),
            })
            .ToList();
        sink.Table(["Type", "State", "Count", "Size"], rows, "Type × State breakdown");
    }

    private static void RenderPoolUtilization(IRenderSink sink,
        IReadOnlyList<(string TypeKey, string ConnKey, int Active, int Total, double UtilPct)> poolGroups)
    {
        if (poolGroups.Count == 0) return;

        var rows = poolGroups.Select(p => new[]
        {
            p.TypeKey.Contains('.') ? p.TypeKey[(p.TypeKey.LastIndexOf('.') + 1)..] : p.TypeKey,
            p.ConnKey.Length > 60 ? p.ConnKey[..57] + "…" : p.ConnKey,
            p.Total.ToString("N0"),
            p.Active.ToString("N0"),
            $"{p.UtilPct:F0}% of {DefaultMaxPool}",
        }).ToList();
        sink.Table(
            ["Type", "Connection String", "Total", "Active", "Pool Utilization"],
            rows,
            $"Pool utilization vs default MaxPoolSize={DefaultMaxPool}");

        var highUtil = poolGroups.Where(p => p.UtilPct >= 80).ToList();
        if (highUtil.Count > 0)
            sink.Alert(AlertLevel.Critical,
                $"{highUtil.Count} connection pool(s) at ≥80% utilization.",
                "Near-full pools cause connection wait timeouts and request queuing.",
                "Increase MaxPoolSize, reduce connection lifetime, or audit for unreleased connections.");
    }

    private static void RenderConnectionStrings(IRenderSink sink, ConnectionPoolData data)
    {
        var connStrGroups = data.Connections
            .Where(c => c.ConnStr.Length > 0)
            .GroupBy(c => c.ConnStr)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new[] { g.Key, g.Count().ToString("N0") })
            .ToList();
        if (connStrGroups.Count > 0)
            sink.Table(["Connection String (masked)", "Count"], connStrGroups,
                "Distinct connection strings (passwords masked)");
    }

    private static void RenderAddresses(IRenderSink sink, ConnectionPoolData data)
    {
        sink.Section("Per-Connection Addresses");
        var rows = data.Connections.Take(200).Select(c => new[]
        {
            $"0x{c.Addr:X16}",
            c.TypeName,
            c.State.Length > 0 ? c.State : "—",
            DumpHelpers.FormatSize(c.Size),
            c.ConnStr.Length > 0 ? c.ConnStr : "—",
        }).ToList();
        sink.Table(["Address", "Type", "State", "Size", "Connection String (masked)"], rows);

        if (data.Connections.Count > 200)
            sink.Alert(AlertLevel.Info, $"Showing first 200 of {data.Connections.Count:N0} connection objects.");
    }

    private static void RenderCommands(IRenderSink sink, ConnectionPoolData data)
    {
        sink.Section("Active SQL Commands");
        if (data.Commands.Count == 0)
        {
            sink.Text("No DbCommand objects with readable command text found.");
            return;
        }

        var cmdGroups = data.Commands
            .GroupBy(c => c.CommandText)
            .Select(g =>
            {
                string typeName  = g.First().TypeName;
                string shortType = typeName.Contains('.') ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName;
                return new[] { g.Count().ToString("N0"), shortType, g.Key };
            })
            .OrderByDescending(r => int.Parse(r[0].Replace(",", "")))
            .ToList();
        sink.Table(["Count", "Type", "Command Text"], cmdGroups,
            $"{data.Commands.Count:N0} DbCommand object(s) with command text — {cmdGroups.Count} distinct queries");

        if (data.Commands.Count > 20)
            sink.Alert(AlertLevel.Warning,
                $"{data.Commands.Count:N0} DbCommand objects with command text found on heap.",
                "Commands should be created, executed, and disposed promptly — not cached on the heap.",
                "Use parameterized queries and dispose SqlCommand objects after use.");
    }
}
