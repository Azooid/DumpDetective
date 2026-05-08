using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class SqlTraceReport
{
    public void Render(SqlTraceData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "SQL/database command analysis — measures query execution time, identifies slow queries, and detects database error patterns from SqlClient and EF Core event sources.",
            why: "Slow queries are one of the leading causes of elevated HTTP latency, GC pressure (from unmaterialized result-set rows), and thread-pool starvation in .NET applications.",
            impact: "A single N+1 query pattern or missing index can multiply database round-trips by orders of magnitude under load. " +
                    "Each blocking database call holds a ThreadPool thread for the full duration.",
            bullets: [
                "Total command time   — cumulative database time across the trace",
                "Slow queries         — individual commands above the slow threshold",
                "Top queries by time  — aggregated query patterns ordered by total cost"
            ],
            action: "Add missing indexes for the top slow queries. " +
                    "Reduce N+1 patterns with .Include() in EF Core or batched queries. " +
                    "Use compiled queries for frequently-executed EF queries."
        );

        sink.Section("Trace Summary", "sql-summary");
        sink.KeyValues([
            ("Trace",              data.TraceInfo),
            ("Total commands",     data.TotalCommands > 0 ? data.TotalCommands.ToString("N0") : "—"),
            ("Total command time", data.TotalCommandMs > 0 ? $"{data.TotalCommandMs:F0} ms" : "—"),
            ("Avg command time",   data.AvgCommandMs > 0 ? $"{data.AvgCommandMs:F2} ms" : "—"),
            ("Max command time",   data.MaxCommandMs > 0 ? $"{data.MaxCommandMs:F1} ms" : "—"),
            ("Error commands",     data.TotalErrors > 0 ? data.TotalErrors.ToString("N0") : "0"),
            ("Slow commands",      $"{data.SlowCommandCount:N0} (>{data.SlowThresholdMs:F0} ms)"),
            ("Process filter",     data.FilteredProcess ?? "(all processes)"),
        ]);

        if (!data.HasData)
        {
            sink.Alert(AlertLevel.Warning, "No SQL command events found in trace.",
                "To capture SQL events, re-collect with one of the following providers:",
                "dotnet-trace:\n" +
                "  dotnet trace collect \\\n" +
                "    --providers 'Microsoft.Data.SqlClient.EventSource:0xFF:4," +
                "System.Data.SqlClient.EventSource:0xFF:4," +
                "Microsoft-EntityFrameworkCore:0xFFFF:5' -p <pid>\n\n" +
                "PerfView:\n" +
                "  /Providers:\"Microsoft.Data.SqlClient.EventSource,Microsoft-EntityFrameworkCore\"");
            return;
        }

        // ── Alerts ────────────────────────────────────────────────────────────
        if (data.MaxCommandMs >= 10_000)
            sink.Alert(AlertLevel.Critical,
                $"Very slow SQL command detected: {data.MaxCommandMs:F0} ms.",
                "A single command took more than 10 seconds. This will block the calling thread for the full duration.",
                "Investigate the query plan, missing indexes, and lock contention on the database server.");
        else if (data.MaxCommandMs >= 2_000)
            sink.Alert(AlertLevel.Warning,
                $"Slow SQL commands detected: max {data.MaxCommandMs:F0} ms.",
                $"{data.SlowCommandCount:N0} command(s) exceeded the {data.SlowThresholdMs:F0} ms threshold.");

        if (data.TotalErrors > 0)
            sink.Alert(AlertLevel.Warning,
                $"{data.TotalErrors:N0} SQL command error(s) detected.",
                "Command errors may indicate connection problems, query timeouts, or constraint violations.",
                "Check the slow/error commands table below for specific details.");

        // Detect whether any commands have no SQL text (commandText was empty in the trace)
        bool hasNoTextCommands = data.TopQueries.Any(q =>
            q.CommandText.StartsWith("(no SQL text", StringComparison.Ordinal));
        if (hasNoTextCommands)
            sink.Alert(AlertLevel.Info,
                "Some commands show '(no SQL text …)' — the SQL query string was not captured in this trace.",
                "The Microsoft-AdoNet-SystemData provider only writes commandText at EventLevel.Verbose (level 5). " +
                "To capture full SQL text, re-collect with a higher verbosity level.",
                "dotnet-trace:  --providers 'Microsoft-AdoNet-SystemData:0xFF:5'\n" +
                "PerfView:      /Providers:\"Microsoft-AdoNet-SystemData\" with /TraceLevel:Verbose\n\n" +
                "The db= and id= values shown in the command column identify which database and SqlCommand " +
                "object each entry came from, even without SQL text.");

        // ── Duration timeline ──────────────────────────────────────────────────
        if (data.DurationTimeline is { Count: > 2 } tl)
            sink.Sparkline(tl, "SQL command time over time (ms/second)", " ms");

        // ── Top databases ──────────────────────────────────────────────────────
        if (data.TopDatabases.Count > 1)
        {
            sink.Section("Command Activity by Database", "sql-databases");
            var rows = new List<string[]>(data.TopDatabases.Count);
            foreach (var db in data.TopDatabases)
                rows.Add([db.Database, db.CommandCount.ToString("N0"), $"{db.TotalMs:F0} ms", $"{db.AvgMs:F1} ms"]);
            sink.Table(
                ["Database", "Command Count", "Total Time", "Avg Time"],
                rows,
                "Databases ordered by total command time");
        }

        // ── Slow commands ──────────────────────────────────────────────────────
        if (data.SlowCommands.Count > 0)
        {
            sink.Section($"Slow Commands (>{data.SlowThresholdMs:F0} ms)", "sql-slow");
            var rows = new List<string[]>(data.SlowCommands.Count);
            foreach (var c in data.SlowCommands)
                rows.Add([c.IsError ? "⚠ Error" : "✓", c.Database,
                          $"{c.DurationMs:F1} ms", c.CommandText]);
            sink.Table(
                ["Status", "Database", "Duration", "Command"],
                rows,
                $"Individual slow commands ordered by duration");
        }

        // ── Top queries by total time ──────────────────────────────────────────
        if (data.TopQueries.Count > 0)
        {
            sink.Section($"Top Queries by Total Execution Time (top {Math.Min(data.TopQueries.Count, top)})", "sql-top");
            var rows = new List<string[]>(data.TopQueries.Count);
            foreach (var q in data.TopQueries)
                rows.Add([q.ExecutionCount.ToString("N0"), $"{q.TotalMs:F0} ms",
                          $"{q.MaxMs:F1} ms", $"{q.AvgMs:F1} ms",
                          q.ErrorCount > 0 ? q.ErrorCount.ToString("N0") : "—",
                          q.CommandText]);
            sink.Table(
                ["Count", "Total Time", "Max", "Avg", "Errors", "Command"],
                rows,
                "Unique query patterns ordered by total execution cost across all executions");
        }
    }
}
