using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Tracing;

namespace Example.DumpDetective;

/// <summary>
/// Correlation rule: HTTP P99 tail latency in the trace + async state machine backlog in the dump.
///
/// Both signals must cross their thresholds before this rule fires:
///   • Trace: HTTP P99 request latency ≥2 000 ms AND ≥10 total requests
///   • Dump:  ≥20 awaiting async state machines AND backlog density ≥2 per alive thread
///
/// When both are present the tail latency is almost certainly caused by async scheduler
/// saturation — new continuations cannot be scheduled promptly because the ThreadPool is
/// processing a backlog of already-queued async state machines.
///
/// The built-in rule confirms HTTP latency × async backlog using slow-request count; this
/// variant uses P99 latency — a direct user-facing SLA metric — and adds "async density"
/// (backlog / alive threads) to measure whether the scheduler is actually saturated or
/// just has a high absolute backlog in a proportionally large application.
///
/// Invocation: DumpDetective trace-dump-analyze app.nettrace app.dmp --with-plugins
/// </summary>
public sealed class HttpP99LatencyVsAsyncDensityRule : ICommand, ITraceDumpCorrelationRule
{
    public string      Name                 => "example.http-p99-vs-async-density";
    public string      Description          => "Correlation rule: HTTP P99 latency spike confirmed by async backlog density in dump.";
    public bool        IncludeInFullAnalyze => false;
    public string      Category             => "Example Plugin";
    public CommandKind Kind                 => CommandKind.Trace;
    public int         Run(string[] args)   => 0;
    public void        Render(DumpContext ctx, IRenderSink sink) { }

    // ── ITraceDumpCorrelationRule ─────────────────────────────────────────────

    public string Key => "example.http-p99-vs-async-density";

    public CorrelationFinding? Evaluate(TraceDumpCorrelationContext ctx)
    {
        if (ctx.Http is null || !ctx.Http.HasData) return null;
        if (ctx.Http.TotalRequests < 10) return null;

        double p99Ms = ctx.Http.P99RequestMs;
        if (p99Ms < 2_000) return null;  // P99 < 2 s — not a tail latency problem

        int asyncBacklog = ctx.Snapshot.AsyncBacklogTotal;
        if (asyncBacklog < 20) return null;

        // Async density: awaiting state machines per alive thread.
        // High density (≥2 per thread) means the scheduler is saturated for this process size.
        // A process with 200 threads and 400 async machines is saturated; one with 200 threads
        // and 21 machines is not — absolute count alone is misleading.
        int aliveThreads = Math.Max(ctx.Snapshot.AliveThreadCount, 1);
        double asyncDensity = (double)asyncBacklog / aliveThreads;
        if (asyncDensity < 2.0) return null;

        int score = 60;
        if (p99Ms >= 10_000)                     score += 10;
        if (asyncDensity >= 10.0)                score += 15;
        if (ctx.Http.ErrorCount > 0)             score += 10;
        if (ctx.Snapshot.TpIdleWorkers < 5)      score += 10;  // near-empty idle pool
        score = Math.Min(score, 94);

        string topPath = ctx.Http.TopPaths.Count > 0
            ? $" Top endpoint: {ctx.Http.TopPaths[0].Path} " +
              $"(avg {ctx.Http.TopPaths[0].AvgDurationMs:F0} ms, max {ctx.Http.TopPaths[0].MaxDurationMs:F0} ms)."
            : "";

        return new CorrelationFinding(
            Severity:          score >= 70 ? FindingSeverity.Warning : FindingSeverity.Info,
            Category:          "HTTP / Async",
            Headline:          $"HTTP P99 latency ({p99Ms / 1_000:F1} s) confirmed by async backlog saturation in dump",
            Detail:            $"Trace: P99 request latency {p99Ms / 1_000:F1} s across {ctx.Http.TotalRequests:N0} requests " +
                               $"({ctx.Http.ErrorCount:N0} errors).{topPath} " +
                               $"Dump: {asyncBacklog:N0} awaiting async state machines " +
                               $"({asyncDensity:F1} per alive thread) indicate scheduler saturation — " +
                               $"new work cannot be scheduled promptly because continuations are already queued.",
            Advice:            "Identify the suspended state machines with `async-stacks --details`. " +
                               "Look for long async chains that block on I/O or lock releases. " +
                               "Increase ThreadPool min-threads if idle worker count is consistently near zero. " +
                               "Run `async-stacks`, `http-trace`, and `thread-pool` for drill-down.",
            Score:             score,
            ContributingAreas: ["http-trace", "dump", "example-plugin"]);
    }
}
