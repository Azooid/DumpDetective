using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Tracing;

namespace Example.DumpDetective;

/// <summary>
/// Correlation rule: slow SQL commands in the trace + live DB connections in the dump.
///
/// Both signals must cross their thresholds before this rule fires:
///   • Trace: ≥3 slow queries AND at least one individual query ≥5 000 ms
///   • Dump:  ≥5 live DB connection objects
///
/// When both are present the dump connections are almost certainly pooled connections
/// stalled behind those timing-out queries. This is distinct from the built-in rule that
/// uses LiveConnectionCount vs SlowCommandCount — this variant requires an actual
/// timeout-level duration to confirm an individual call is hanging rather than just slow.
///
/// Invocation: DumpDetective trace-dump-analyze app.nettrace app.dmp --with-plugins
/// </summary>
public sealed class SqlTimeoutVsConnectionCountRule : ICommand, ITraceDumpCorrelationRule
{
    public string      Name                 => "example.sql-timeout-vs-connections";
    public string      Description          => "Correlation rule: individual SQL timeout (>5 s) combined with live connection count in dump.";
    public bool        IncludeInFullAnalyze => false;
    public string      Category             => "Example Plugin";
    public CommandKind Kind                 => CommandKind.Trace;
    public int         Run(string[] args)   => 0;  // purely a rule host — no standalone analysis
    public void        Render(DumpContext ctx, IRenderSink sink) { }

    // ── ITraceDumpCorrelationRule ─────────────────────────────────────────────

    public string Key => "example.sql-timeout-vs-connections";

    public CorrelationFinding? Evaluate(TraceDumpCorrelationContext ctx)
    {
        if (ctx.Sql is null || !ctx.Sql.HasData) return null;
        if (ctx.Sql.SlowCommandCount < 3) return null;

        // Rule fires when at least one individual query exceeded 5 000 ms —
        // this indicates a hard timeout scenario rather than just slow queries.
        double worstSlowMs = ctx.Sql.MaxCommandMs;
        if (worstSlowMs < 5_000) return null;

        int liveConns = ctx.Snapshot.ConnectionCount;
        if (liveConns < 5) return null;

        // Score: higher when connections are piling up AND queries are very slow.
        int score = 60;
        if (ctx.Sql.SlowCommandCount >= 10) score += 10;
        if (worstSlowMs >= 15_000)          score += 10;
        if (liveConns >= 20)                score += 10;
        if (ctx.Sql.TotalErrors > 0)        score += 10;
        score = Math.Min(score, 95);

        string topDb = ctx.Sql.TopDatabases.Count > 0
            ? $" Database: {ctx.Sql.TopDatabases[0].Database}." : "";

        return new CorrelationFinding(
            Severity:          score >= 75 ? FindingSeverity.Warning : FindingSeverity.Info,
            Category:          "SQL / Connections",
            Headline:          "SQL query timeouts coincide with elevated live connection count",
            Detail:            $"{ctx.Sql.SlowCommandCount:N0} slow SQL command(s) detected in trace " +
                               $"(worst: {worstSlowMs / 1_000:F1} s, threshold: {ctx.Sql.SlowThresholdMs / 1_000:F0} s). " +
                               $"Dump shows {liveConns:N0} live DB connections — likely pooled connections " +
                               $"stalled behind these long-running queries.{topDb}",
            Advice:            "Check query plans for missing indexes or parameter sniffing. " +
                               "Review connection pool max size (`Max Pool Size` in the connection string). " +
                               "Consider adding a command timeout shorter than the default 30 s to fail fast. " +
                               "Run `connection-pool` and `sql-trace` for full detail.",
            Score:             score,
            ContributingAreas: ["sql-trace", "dump", "example-plugin"]);
    }
}
