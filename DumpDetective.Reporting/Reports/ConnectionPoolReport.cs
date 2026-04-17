using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class ConnectionPoolReport
{
    private const int DefaultMaxPool = 100;

    public void Render(ConnectionPoolData data, IRenderSink sink, bool showAddr = false)
    {
        if (data.Connections.Count == 0) { sink.Text("No database connection objects found."); return; }

        var groups = data.Connections
            .GroupBy(c => (c.TypeName, c.ConnStr))
            .Select(g =>
            {
                int active = g.Count(c => c.State is "Open" or "Executing" or "Fetching");
                return (TypeKey: g.Key.TypeName, ConnKey: MaskPassword(g.Key.ConnStr),
                    Active: active, Total: g.Count(),
                    UtilPct: active * 100.0 / DefaultMaxPool);
            })
            .OrderByDescending(g => g.UtilPct)
            .ToList();

        int totalActive = groups.Sum(g => g.Active);
        int totalConns  = data.Connections.Count;

        sink.Section("Summary");
        sink.KeyValues([
            ("Total connection objects", totalConns.ToString("N0")),
            ("Active connections",       totalActive.ToString("N0")),
            ("Assumed MaxPoolSize",       DefaultMaxPool.ToString("N0")),
            ("Distinct connection strings", groups.Count.ToString("N0")),
        ]);

        var exhausted = groups.Where(g => g.UtilPct >= 80).ToList();
        if (exhausted.Count > 0)
            sink.Alert(AlertLevel.Critical, $"{exhausted.Count} pool(s) at ≥ 80% utilization.",
                "Near-exhausted connection pools cause request timeouts waiting for a free connection.",
                "Increase MaxPoolSize, fix connection leaks, or reduce concurrent database load.");

        RenderTypeStateTable(sink, data);
        RenderPoolUtilization(sink, groups);
        RenderConnectionStrings(sink, data);
        if (showAddr) RenderAddresses(sink, data);
    }

    private static void RenderTypeStateTable(IRenderSink sink, ConnectionPoolData data)
    {
        sink.Section("Connections by Type and State");
        var rows = data.Connections
            .GroupBy(c => (c.TypeName, c.State))
            .Select(g => new[] { g.Key.TypeName, g.Key.State, g.Count().ToString("N0") })
            .OrderBy(r => r[0]).ThenBy(r => r[1])
            .ToList();
        sink.Table(["Type", "State", "Count"], rows);
    }

    private static void RenderPoolUtilization(IRenderSink sink,
        IEnumerable<(string TypeKey, string ConnKey, int Active, int Total, double UtilPct)> groups)
    {
        sink.Section("Pool Utilization");
        var rows = groups.Select(g => new[]
        {
            g.TypeKey.Split('.').Last(),
            g.ConnKey.Length > 60 ? g.ConnKey[..57] + "…" : g.ConnKey,
            g.Active.ToString("N0"),
            g.Total.ToString("N0"),
            $"{g.UtilPct:F1}% of {DefaultMaxPool}",
        }).ToList();
        sink.Table(["Type", "Connection String (masked)", "Active", "Total", "Utilization"], rows,
            $"Assumed MaxPoolSize = {DefaultMaxPool};  active = Open/Executing/Fetching");
    }

    private static void RenderConnectionStrings(IRenderSink sink, ConnectionPoolData data)
    {
        var distinct = data.Connections
            .Where(c => c.ConnStr.Length > 0)
            .Select(c => MaskPassword(c.ConnStr))
            .Distinct()
            .ToList();
        if (distinct.Count == 0) return;

        sink.Section("Connection Strings (passwords masked)");
        sink.Table(["Connection String"], distinct.Select(s => new[] { s }).ToList());
    }

    private static void RenderAddresses(IRenderSink sink, ConnectionPoolData data)
    {
        sink.Section("Connection Objects (up to 200)");
        var rows = data.Connections.Take(200)
            .Select(c => new[] { c.TypeName, $"0x{c.Addr:X16}", c.State,
                c.Size > 0 ? $"{c.Size:N0} B" : "—" }).ToList();
        sink.Table(["Type", "Address", "State", "Size"], rows);
    }

    private static string MaskPassword(string connStr)
    {
        if (string.IsNullOrEmpty(connStr)) return connStr;
        // Replace common password tokens
        return System.Text.RegularExpressions.Regex.Replace(
            connStr,
            @"(?i)(password|pwd|pass)\s*=\s*[^;]+",
            m => m.Groups[1].Value + "=***");
    }
}
