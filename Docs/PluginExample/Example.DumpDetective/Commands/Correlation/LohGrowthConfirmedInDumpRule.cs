using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Tracing;

namespace Example.DumpDetective;

/// <summary>
/// Correlation rule: LOH is growing in the trace AND LOH is fragmented in the dump.
///
/// Both signals must cross their thresholds before this rule fires:
///   • Trace: LOH is trending upward AND growth ≥50 MB
///   • Dump:  LOH fragmentation ≥15% AND LOH size ≥50 MB
///
/// When both are present the growth observed during the trace has materialised as
/// fragmented LOH at dump time — meaning the runtime has not been able to reclaim
/// or compact it. Fragmented LOH cannot be compacted by default and will persist
/// until the process recycles or GCSettings.LargeObjectHeapCompactionMode is set.
///
/// The built-in LOH rule checks fragmentation vs GC pause. This rule focuses on
/// confirming trace-observed growth is still present in the dump as proof that
/// compaction did not occur.
///
/// Invocation: DumpDetective trace-dump-analyze app.nettrace app.dmp --with-plugins
/// </summary>
public sealed class LohGrowthConfirmedInDumpRule : ICommand, ITraceDumpCorrelationRule
{
    public string      Name                 => "example.loh-growth-vs-dump-frag";
    public string      Description          => "Correlation rule: LOH trend growth in trace confirmed by LOH fragmentation in dump.";
    public bool        IncludeInFullAnalyze => false;
    public string      Category             => "Example Plugin";
    public CommandKind Kind                 => CommandKind.Trace;
    public int         Run(string[] args)   => 0;
    public void        Render(DumpContext ctx, IRenderSink sink) { }

    // ── ITraceDumpCorrelationRule ─────────────────────────────────────────────

    public string Key => "example.loh-growth-vs-dump-frag";

    public CorrelationFinding? Evaluate(TraceDumpCorrelationContext ctx)
    {
        if (ctx.Loh is null || !ctx.Loh.HasData) return null;
        if (!ctx.Loh.IsTrendingUp) return null;

        long growthMb = ctx.Loh.LohGrowthBytes / (1024 * 1024);
        if (growthMb < 50) return null;  // < 50 MB growth — not worth flagging

        double lohFragPct = ctx.Snapshot.LohFragmentationPct;
        if (lohFragPct < 15.0) return null;  // LOH compacted or not fragmented

        // Also confirm the dump LOH is meaningfully large — tiny LOHs can have high
        // fragmentation pct by coincidence and are not actionable.
        if (ctx.Snapshot.LohBytes < 50L * 1024 * 1024) return null;

        int score = 55;
        if (growthMb >= 200)                     score += 15;
        if (lohFragPct >= 30.0)                  score += 15;
        if (ctx.Loh.Gen2GcsWithLohGrowth >= 10)  score += 10;
        score = Math.Min(score, 93);

        long lohMb     = ctx.Snapshot.LohBytes / (1024 * 1024);
        long lohFreeMb = ctx.Snapshot.LohFreeBytes / (1024 * 1024);

        return new CorrelationFinding(
            Severity:          score >= 70 ? FindingSeverity.Warning : FindingSeverity.Info,
            Category:          "GC / LOH",
            Headline:          "LOH growth in trace confirmed as fragmented LOH in dump",
            Detail:            $"Trace: LOH grew by {growthMb:N0} MB across {ctx.Loh.TotalGcCount:N0} GC events " +
                               $"({ctx.Loh.Gen2GcsWithLohGrowth} Gen2 GCs with positive growth). " +
                               $"Dump: LOH is {lohMb:N0} MB with {lohFreeMb:N0} MB free ({lohFragPct:F1}% fragmented). " +
                               $"Fragmented LOH cannot be compacted by default, so the growth is permanent until " +
                               $"the process recycles or LOH compaction is explicitly triggered.",
            Advice:            "Switch large array allocations to ArrayPool<T> or MemoryPool<T>. " +
                               "Enable LOH compaction for a single GC via GCSettings.LargeObjectHeapCompactionMode. " +
                               "Avoid frequent large byte[]/char[] allocations in hot paths. " +
                               "Run `loh-trace` for growth timeline and `heap-fragmentation` for the per-segment breakdown.",
            Score:             score,
            ContributingAreas: ["loh-trace", "dump", "example-plugin"]);
    }
}
