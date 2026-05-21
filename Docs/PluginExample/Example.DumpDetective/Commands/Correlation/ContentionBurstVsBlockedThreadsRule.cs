using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Tracing;

namespace Example.DumpDetective;

/// <summary>
/// Correlation rule: dense lock contention burst in the trace + blocked threads in the dump.
///
/// Both signals must cross their thresholds before this rule fires:
///   • Trace: ≥20 contention events AND contention rate ≥5 events/second
///   • Dump:  ≥5 blocked threads
///
/// This variant uses the contention RATE (events per second of trace duration) rather
/// than total count. A tight burst at high rate is more actionable than the same number
/// of events spread across minutes — it indicates a request storm hitting a single lock.
/// The built-in rule triggers on total contention count + starvation events; this rule
/// specifically catches rate-based saturation that can be below the built-in total threshold.
///
/// Invocation: DumpDetective trace-dump-analyze app.nettrace app.dmp --with-plugins
/// </summary>
public sealed class ContentionBurstVsBlockedThreadsRule : ICommand, ITraceDumpCorrelationRule
{
    public string      Name                 => "example.contention-burst-vs-blocked";
    public string      Description          => "Correlation rule: dense lock contention burst in trace + blocked threads in dump.";
    public bool        IncludeInFullAnalyze => false;
    public string      Category             => "Example Plugin";
    public CommandKind Kind                 => CommandKind.Trace;
    public int         Run(string[] args)   => 0;
    public void        Render(DumpContext ctx, IRenderSink sink) { }

    // ── ITraceDumpCorrelationRule ─────────────────────────────────────────────

    public string Key => "example.contention-burst-vs-blocked";

    public CorrelationFinding? Evaluate(TraceDumpCorrelationContext ctx)
    {
        if (ctx.Contention is null) return null;
        if (ctx.Contention.TotalContentions < 20) return null;

        // Compute contention burst intensity: events per second of trace duration.
        // Estimate trace duration from CPU stats if available, otherwise from total wait
        // time (assumed to be ~50% of trace time) with a 60 s fallback.
        double totalTraceMs = ctx.Cpu?.Stats?.TraceDurationMs
            ?? (ctx.Contention.TotalWaitMs > 0 ? ctx.Contention.TotalWaitMs * 2 : 60_000);
        double ratePerSec = totalTraceMs > 0
            ? ctx.Contention.TotalContentions / (totalTraceMs / 1_000.0)
            : 0;
        if (ratePerSec < 5.0) return null;  // < 5 contentions/sec — not a burst

        int blockedThreads = ctx.Snapshot.BlockedThreadCount;
        if (blockedThreads < 5) return null;

        int score = 55;
        if (ratePerSec >= 50)                      score += 15;
        if (blockedThreads >= 20)                  score += 15;
        if (ctx.Contention.TotalWaitMs >= 10_000)  score += 10;
        score = Math.Min(score, 92);

        string hotspot = ctx.Contention.Hotspots.Count > 0
            ? $" Hottest lock site: {TrimFrame(ctx.Contention.Hotspots[0].Location)}." : "";

        return new CorrelationFinding(
            Severity:          score >= 70 ? FindingSeverity.Warning : FindingSeverity.Info,
            Category:          "Contention / Threads",
            Headline:          "Dense lock contention burst in trace aligns with blocked threads in dump",
            Detail:            $"Contention rate: {ratePerSec:F1}/s ({ctx.Contention.TotalContentions:N0} events, " +
                               $"{ctx.Contention.TotalWaitMs:F0} ms total wait). " +
                               $"Dump shows {blockedThreads:N0} blocked threads — many are likely stalled " +
                               $"on the same lock that produced the burst.{hotspot}",
            Advice:            "Identify the hot lock in the contention hotspot list and either reduce critical " +
                               "section scope, replace Monitor with SemaphoreSlim/Channel<T> for async code, " +
                               "or use lock-free data structures (ConcurrentDictionary, ImmutableList). " +
                               "Run `contention-trace` and `thread-analysis` for the full picture.",
            Score:             score,
            ContributingAreas: ["contention-trace", "dump", "example-plugin"]);
    }

    private static string TrimFrame(string frame)
        => frame.Length > 80 ? frame[..77] + "..." : frame;
}
