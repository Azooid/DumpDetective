using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class ConnectionPoolTraceReport
{
    public void Render(ConnectionPoolTraceData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "Database connection pool analysis from SqlConnection Open/Close events — tracks open/close pairs, detects leaked connections, and measures peak concurrency.",
            why: "Connection pool exhaustion causes new database operations to block indefinitely waiting for a connection, creating cascading latency and thread starvation.",
            impact: "A single leaked connection reduces the available pool. At 100 leaked connections the default pool is exhausted and all new operations throw SqlException.",
            bullets: [
                "Leaked connections  — opens without matching close within the trace",
                "Peak concurrent     — maximum simultaneous open connections observed",
                "Top databases       — databases with most connection churn"
            ],
            action: "Ensure SqlConnection is disposed via 'using' statement. " +
                    "Increase pool size (Max Pool Size in connection string) if legitimately needed. " +
                    "Set connection timeouts to fail fast rather than block indefinitely."
        );

        sink.Section("Trace Summary", "connpool-summary");
        sink.KeyValues([
            ("Trace",                 data.TraceInfo),
            ("Total opens",           data.TotalOpens.ToString("N0")),
            ("Total closes",          data.TotalCloses.ToString("N0")),
            ("Peak concurrent",       data.PeakOpenConnections.ToString("N0")),
            ("Leaked connections",     data.LeakedConnections > 0
                                        ? $"{data.LeakedConnections} ⚠" : "0"),
            ("Process filter",        data.FilteredProcess ?? "(all processes)"),
        ]);

        if (!data.HasData)
        {
            sink.Alert(AlertLevel.Info, "No SqlConnection events found.",
                "Collect with: --providers 'Microsoft.Data.SqlClient.EventSource:0xFF:5,System.Data.SqlClient.EventSource:0xFF:5'");
            return;
        }

        if (data.LeakedConnections > 0)
            sink.Alert(AlertLevel.Critical,
                $"{data.LeakedConnections} connection(s) opened without a matching close.",
                "These connections are not returned to the pool and will exhaust it over time.",
                "Ensure SqlConnection is always disposed: use 'using var conn = new SqlConnection(cs);'");

        if (data.PeakOpenConnections > 80)
            sink.Alert(AlertLevel.Warning,
                $"Peak concurrent connections: {data.PeakOpenConnections} (default pool size = 100).",
                "Approaching pool exhaustion. Audit connection lifecycle and consider increasing Max Pool Size.");

        if (data.ConnectionTimeline is { Count: > 2 } tl)
            sink.Sparkline(tl, "Open connections over time", "");

        if (data.TopDatabases.Count > 0)
        {
            sink.Section("Connection Activity by Database", "connpool-databases");
            var rows = new List<string[]>(data.TopDatabases.Count);
            foreach (var db in data.TopDatabases.Take(top))
                rows.Add([db.Database, db.Opens.ToString("N0"), db.Closes.ToString("N0"),
                           db.PeakConcurrent.ToString("N0"), db.NetOpen.ToString("N0")]);
            sink.Table(
                ["Database", "Opens", "Closes", "Peak Concurrent", "Net Open"],
                rows, "Databases ordered by opens count");
        }
    }
}
