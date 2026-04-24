using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

/// <summary>
/// Renders the multi-dump trend report and diagnosis summary.
/// Called by <c>TrendAnalysisCommand</c> and <c>TrendRenderCommand</c>.
/// </summary>
public static class TrendAnalysisReport
{
    // ── Public entry point ────────────────────────────────────────────────────

    public static void RenderTrend(
        List<DumpSnapshot>     snaps,
        IRenderSink            sink,
        IReadOnlyList<string>? ignoreEventTypes = null,
        int                    baselineIndex    = 0,
        string                 dumpPrefix       = "D")
    {
        baselineIndex = Math.Clamp(baselineIndex, 0, snaps.Count - 1);

        var    s0    = snaps[baselineIndex];
        var    sN    = snaps[^1];
        bool   full  = snaps.Any(s => s.IsFullMode);
        var    labels = snaps.Select((_, i) => $"{dumpPrefix}{i + 1}").ToArray();
        string baselineLabel = labels[baselineIndex];
        string baselineNote  = baselineIndex == 0 ? "" : $"  |  Baseline: {baselineLabel}";

        sink.Header(
            "Dump Detective \u2014 Trend Analysis Report",
            $"{snaps.Count} dumps  |  {snaps[0].FileTime:yyyy-MM-dd HH:mm} \u2192 {sN.FileTime:yyyy-MM-dd HH:mm}  |  {(full ? "Full" : "Lightweight")} mode{baselineNote}");

        // ── Diagnosis Summary ─────────────────────────────────────────────────
        sink.Section("Diagnosis Summary");
        sink.Explain(
            what: "A cross-snapshot comparison of all key health signals. Each row shows how a metric evolved " +
                  "from the baseline dump to the most recent dump, with severity classification.",
            why:  "Comparing metrics across time is the most reliable way to distinguish temporary workload spikes " +
                  "from genuine accumulation patterns. A single elevated snapshot may be normal. " +
                  "Consistently elevated or worsening metrics indicate unresolved conditions.",
            bullets:
            [
                "✗ Critical — the metric exceeded the critical threshold in the latest dump",
                "⚠ Warning — the metric exceeded the warning threshold — monitor for further degradation",
                "✓ Good — the metric is within acceptable range",
                "A stable ✗ CRITICAL metric does NOT mean it improved — it may have been critical throughout",
                "↑↑ worsening trend = metric increased significantly from baseline to latest",
                "↓ improving trend = metric decreased significantly from baseline to latest",
                "~ stable = metric changed minimally (may still be critical if it was already elevated)",
            ],
            impact: "Worsening trends in critical metrics indicate the application's condition is degrading. " +
                    "Stable critical metrics indicate a persistent unresolved problem. " +
                    "Only ↓ improving trends with ✓ Good classification indicate genuine recovery.");
        RenderDiagnosisSummary(snaps, s0, sN, labels, baselineLabel, sink);

        // ── 0. Dump Timeline ──────────────────────────────────────────────────
        sink.Section("0. Dump Timeline");
        sink.Explain(
            what: "The temporal sequence and metadata of all analyzed dump snapshots.",
            why:  "Understanding the capture timeline helps correlate diagnostic symptoms with specific times. " +
                  "Dumps taken shortly apart that show persistent critical metrics indicate a chronic problem, " +
                  "not a transient workload spike.",
            bullets:
            [
                "Closely spaced dumps with worsening metrics → rapid accumulation or escalating incident",
                "Large time gaps → conditions may have changed significantly between captures",
                "Consistent health score across all dumps → the issue was present throughout the entire window",
            ]);
        sink.Table(
            ["Dump", "File", "File Size", "Time", "Threads (Total / Alive)", "Health"],
            snaps.Select((s, i) => new[]
            {
                labels[i],
                Path.GetFileName(s.DumpPath),
                DumpHelpers.FormatSize(s.DumpFileSizeBytes),
                s.FileTime.ToString("HH:mm"),
                $"{s.ThreadCount} / {s.AliveThreadCount}",
                $"{s.HealthScore}/100  {ScoreLabel(s.HealthScore)}",
            }).ToList());

        // ── 1. Incident Summary ───────────────────────────────────────────────
        sink.Section("1. Incident Summary");
        sink.Explain(
            what: "A scored summary of all monitored signals in the latest dump compared to configured thresholds, " +
                  "plus finding-level changes between baseline and latest.",
            why:  "This section translates raw metric values into operational severity levels. It shows not just " +
                  "what the current values are, but which conditions are new (appeared since baseline) and " +
                  "which resolved.",
            bullets:
            [
                "New findings (🆕) — conditions that were not present in the baseline dump",
                "Resolved findings (✅) — conditions that were present in baseline but not in the latest dump",
                "Findings present in all dumps → chronic unresolved conditions, not temporary spikes",
            ],
            action: "Focus investigation on findings present in the latest dump and on metrics with ↑↑ worsening trends. " +
                    "Resolved findings may indicate temporary workload spikes that self-corrected.");

        {
            static string Status(double val, double warnAt, double critAt, bool higherIsBad = true)
            {
                if (higherIsBad) return val >= critAt ? "✗" : val >= warnAt ? "⚠" : "✓";
                else             return val <= critAt ? "✗" : val <= warnAt ? "⚠" : "✓";
            }

            string Cell(double val, string display, double warnAt, double critAt, bool higherIsBad = true)
                => $"{Status(val, warnAt, critAt, higherIsBad)} {display}";

            var incidentCols = new[] { "Signal" }.Concat(labels).Append($"Trend ({baselineLabel}→{labels[^1]})").ToArray();
            var incidentRows = new List<string[]>();

            var tt = ThresholdLoader.Current.Trend;

            void IR(string name, Func<DumpSnapshot, (double val, string display)> proj,
                    double warnAt, double critAt, bool higherIsBad = true)
            {
                var cells = snaps.Select(s => { var (v, d) = proj(s); return Cell(v, d, warnAt, critAt, higherIsBad); }).ToArray();
                var (v0, _) = proj(s0); var (vN, _) = proj(sN);
                incidentRows.Add([name, .. cells, Trend(v0, vN, higherIsBad)]);
            }

            IR("Health Score",
                s => (s.HealthScore, $"{s.HealthScore}/100 {ScoreLabel(s.HealthScore)}"),
                warnAt: tt.ScoreWarn, critAt: tt.ScoreCrit, higherIsBad: false);
            IR("Heap Total",
                s => (s.TotalHeapBytes / 1048576.0, DumpHelpers.FormatSize(s.TotalHeapBytes)),
                tt.HeapWarnMb, tt.HeapCritMb);
            IR("LOH Size",
                s => (s.LohBytes / 1048576.0, DumpHelpers.FormatSize(s.LohBytes)),
                tt.LohWarnMb, tt.LohCritMb);
            IR("Fragmentation",
                s => (s.HeapFreeBytes / 1048576.0, $"{s.FragmentationPct:F1}% ({DumpHelpers.FormatSize(s.HeapFreeBytes)})"),
                tt.FragWarnMb, tt.FragCritMb);
            IR("Blocked Threads",
                s => (s.BlockedThreadCount, s.BlockedThreadCount.ToString("N0")),
                tt.BlockedWarn, tt.BlockedCrit);
            IR("Async Backlog",
                s => (s.AsyncBacklogTotal, s.AsyncBacklogTotal.ToString("N0")),
                tt.AsyncWarn, tt.AsyncCrit);
            IR("Active Exceptions",
                s => (s.ExceptionThreadCount, s.ExceptionThreadCount.ToString("N0")),
                tt.ExceptionWarn, tt.ExceptionCrit);
            IR("Finalizer Queue",
                s => (s.FinalizerQueueDepth, s.FinalizerQueueDepth.ToString("N0")),
                tt.FinalizerWarn, tt.FinalizerCrit);
            IR("Timer Objects",
                s => (s.TimerCount, s.TimerCount.ToString("N0")),
                tt.TimerWarn, tt.TimerCrit);
            if (snaps.Any(s => s.WcfObjectCount > 0))
                IR("WCF Faulted Channels",
                    s => (s.WcfFaultedCount, s.WcfFaultedCount == 0 ? "—" : s.WcfFaultedCount.ToString("N0")),
                    tt.WcfWarn, tt.WcfCrit);
            if (snaps.Any(s => s.ConnectionCount > 0))
                IR("DB Connections",
                    s => (s.ConnectionCount, s.ConnectionCount.ToString("N0")),
                    tt.DbWarn, tt.DbCrit);
            IR("Pinned Handles",
                s => (s.PinnedHandleCount, s.PinnedHandleCount.ToString("N0")),
                tt.PinnedWarn, tt.PinnedCrit);
            IR("Event Subscribers",
                s => (s.EventSubscriberTotal, s.EventSubscriberTotal.ToString("N0")),
                tt.EventWarn, tt.EventCrit);
            if (snaps.Any(s => s.StringWastedBytes > 0))
                IR("String Waste",
                    s => (s.StringWastedBytes / 1048576.0, DumpHelpers.FormatSize(s.StringWastedBytes)),
                    tt.StringWasteWarnMb, tt.StringWasteCritMb);

            // Memory leak signal
            {
                var leakSignals = snaps.Select((s, _i) =>
                {
                    double gen2Pct = s.TotalHeapBytes > 0 ? s.Gen2Bytes * 100.0 / s.TotalHeapBytes : 0;
                    bool hasNewType = s.TopTypes.Any(t => t.Count >= 500 && s0.TopTypes.All(b => b.Name != t.Name));
                    bool explosiveGrowth = s.TopTypes.Any(t =>
                    {
                        var bType = s0.TopTypes.FirstOrDefault(b => b.Name == t.Name);
                        return bType is not null && bType.Count > 0 &&
                               (t.Count - bType.Count) * 100.0 / bType.Count > 50 &&
                               t.Count >= 500;
                    });

                    int score2;
                    string display;
                    if (gen2Pct > 50 || hasNewType)
                    {
                        score2  = 2;
                        display = gen2Pct > 50 ? $"Gen2 {gen2Pct:F0}%" : "new type(s) ↑↑";
                    }
                    else if (gen2Pct > 30 || explosiveGrowth)
                    {
                        score2  = 1;
                        display = gen2Pct > 30 ? $"Gen2 {gen2Pct:F0}%" : "growth ↑↑";
                    }
                    else
                    {
                        score2  = 0;
                        display = $"Gen2 {gen2Pct:F0}%";
                    }
                    string icon = score2 == 2 ? "✗" : score2 == 1 ? "⚠" : "✓";
                    return (score2, display: $"{icon} {display}");
                }).ToArray();

                double leakScoreBaseline = leakSignals[baselineIndex].score2;
                double leakScoreLatest   = leakSignals[^1].score2;
                string leakTrend = leakScoreLatest > leakScoreBaseline ? "↑↑ worsening"
                                 : leakScoreLatest < leakScoreBaseline ? "↓ improving"
                                 : leakScoreLatest >= 2 ? "↑↑ persistent"
                                 : "~";

                incidentRows.Add(["Memory Leak Suspects",
                    .. leakSignals.Select(x => x.display),
                    leakTrend]);
            }

            // Exec summary
            var span      = sN.FileTime - snaps[0].FileTime;
            string spanStr = span.TotalHours >= 1 ? $"{span.TotalHours:F1} hours" : $"{span.TotalMinutes:F0} minutes";
            int scoreChange = sN.HealthScore - s0.HealthScore;
            string scoreDelta = scoreChange == 0 ? "no change" : scoreChange > 0 ? $"+{scoreChange} pts" : $"{scoreChange} pts";
            string scoreArrow = scoreChange > 0 ? "↑" : scoreChange < 0 ? "↓" : "~";

            var criticals = new List<string>();
            var warnings  = new List<string>();

            void CheckSignal(string name, double val, double warnAt, double critAt, bool higherIsBad = true)
            {
                var st2 = Status(val, warnAt, critAt, higherIsBad);
                if      (st2 == "✗") criticals.Add(name);
                else if (st2 == "⚠") warnings.Add(name);
            }

            CheckSignal("Health Score",      sN.HealthScore,                warnAt: tt.ScoreWarn,        critAt: tt.ScoreCrit,        higherIsBad: false);
            CheckSignal("Heap Total",        sN.TotalHeapBytes / 1048576.0, tt.HeapWarnMb,              tt.HeapCritMb);
            CheckSignal("LOH Size",          sN.LohBytes / 1048576.0,       tt.LohWarnMb,               tt.LohCritMb);
            CheckSignal("Fragmentation",     sN.HeapFreeBytes / 1048576.0,  tt.FragWarnMb,              tt.FragCritMb);
            CheckSignal("Blocked Threads",   sN.BlockedThreadCount,         tt.BlockedWarn,             tt.BlockedCrit);
            CheckSignal("Async Backlog",     sN.AsyncBacklogTotal,          tt.AsyncWarn,               tt.AsyncCrit);
            CheckSignal("Active Exceptions", sN.ExceptionThreadCount,       tt.ExceptionWarn,           tt.ExceptionCrit);
            CheckSignal("Finalizer Queue",   sN.FinalizerQueueDepth,        tt.FinalizerWarn,           tt.FinalizerCrit);
            CheckSignal("Timer Objects",     sN.TimerCount,                 tt.TimerWarn,               tt.TimerCrit);
            if (snaps.Any(s => s.WcfObjectCount > 0))
                CheckSignal("WCF Faulted Channels", sN.WcfFaultedCount,    tt.WcfWarn,                 tt.WcfCrit);
            if (snaps.Any(s => s.ConnectionCount > 0))
                CheckSignal("DB Connections",       sN.ConnectionCount,    tt.DbWarn,                  tt.DbCrit);
            CheckSignal("Pinned Handles",    sN.PinnedHandleCount,          tt.PinnedWarn,              tt.PinnedCrit);
            CheckSignal("Event Subscribers", sN.EventSubscriberTotal,       tt.EventWarn,               tt.EventCrit);
            if (snaps.Any(s => s.StringWastedBytes > 0))
                CheckSignal("String Waste",  sN.StringWastedBytes / 1048576.0, tt.StringWasteWarnMb,   tt.StringWasteCritMb);

            // Memory leak signal — same logic as the per-dump table row
            {
                double gen2PctN      = sN.TotalHeapBytes > 0 ? sN.Gen2Bytes * 100.0 / sN.TotalHeapBytes : 0;
                bool   hasNewTypeN   = sN.TopTypes.Any(t => t.Count >= 500 && s0.TopTypes.All(b => b.Name != t.Name));
                bool   hasExplosiveN = sN.TopTypes.Any(t =>
                {
                    var b = s0.TopTypes.FirstOrDefault(x => x.Name == t.Name);
                    return b is not null && b.Count > 0 &&
                           (t.Count - b.Count) * 100.0 / b.Count > 50 && t.Count >= 500;
                });
                if      (gen2PctN > 50 || hasNewTypeN)    criticals.Add("Memory Leak Suspects");
                else if (gen2PctN > 30 || hasExplosiveN)  warnings.Add("Memory Leak Suspects");
            }

            var newFindings = sN.Findings
                .Where(f => !s0.Findings.Any(x => x.Category == f.Category && x.Headline == f.Headline))
                .ToList();
            var resolvedFindings = s0.Findings
                .Where(f => !sN.Findings.Any(x => x.Category == f.Category && x.Headline == f.Headline))
                .ToList();

            sink.KeyValues(
            [
                ("Dumps Analyzed", snaps.Count.ToString()),
                ("Time Span",      $"{spanStr}  ({snaps[0].FileTime:yyyy-MM-dd HH:mm} → {sN.FileTime:yyyy-MM-dd HH:mm})"),
                ("Comparison",     $"{labels[^1]} vs baseline {baselineLabel}"),
                ("Health Score",   $"{s0.HealthScore}/100 {ScoreLabel(s0.HealthScore)}  →  {sN.HealthScore}/100 {ScoreLabel(sN.HealthScore)}  ({scoreArrow} {scoreDelta})"),
            ]);

            if (criticals.Count > 0)
                sink.Alert(AlertLevel.Critical,
                    $"Critical signals ({criticals.Count}): {string.Join(" · ", criticals)}",
                    detail: "These signals exceeded critical thresholds in the latest dump. Immediate investigation and remediation is recommended.");
            else if (warnings.Count == 0)
                sink.Alert(AlertLevel.Info,
                    "All monitored signals are within acceptable thresholds.",
                    detail: "No immediate action required. Continue monitoring.");

            if (warnings.Count > 0)
                sink.Alert(AlertLevel.Warning,
                    $"Signals to monitor ({warnings.Count}): {string.Join(" · ", warnings)}",
                    detail: "These signals exceeded warning thresholds but are not yet critical. Monitor for further degradation.");

            sink.Table(incidentCols, incidentRows, "✓ good  ⚠ warning  ✗ critical  (thresholds are heuristic)");

            // Finding details
            var allFindings2 = snaps.SelectMany(s => s.Findings.Select(f => (f.Severity, f.Category, f.Headline)))
                .Distinct()
                .OrderBy(f => f.Severity == FindingSeverity.Critical ? 0 : f.Severity == FindingSeverity.Warning ? 1 : 2)
                .ThenBy(f => f.Category)
                .ThenBy(f => f.Headline)
                .ToList();

            if (allFindings2.Count > 0)
            {
                int dumpIdx = 0;
                foreach (var (s, lbl) in snaps.Zip(labels))
                {
                    if (s.Findings.Count == 0) { dumpIdx++; continue; }
                    int critCount = s.Findings.Count(f => f.Severity == FindingSeverity.Critical);
                    int warnCount = s.Findings.Count(f => f.Severity == FindingSeverity.Warning);
                    int infoCount = s.Findings.Count - critCount - warnCount;
                    var badge = $"{(critCount > 0 ? $"  ✗ {critCount} critical" : "")}" +
                                $"{(warnCount > 0 ? $"  ⚠ {warnCount} warning"  : "")}" +
                                $"{(infoCount  > 0 ? $"  ℹ {infoCount} info"     : "")}";
                    sink.BeginDetails($"{lbl}  —  Score {s.HealthScore}/100 {ScoreLabel(s.HealthScore)}{badge}", open: dumpIdx == 0);
                    foreach (var f in s.Findings
                        .OrderBy(f => f.Severity == FindingSeverity.Critical ? 0 : f.Severity == FindingSeverity.Warning ? 1 : 2)
                        .ThenBy(f => f.Category))
                    {
                        var lvl = f.Severity == FindingSeverity.Critical ? AlertLevel.Critical
                                : f.Severity == FindingSeverity.Warning  ? AlertLevel.Warning
                                :                                          AlertLevel.Info;
                        sink.Alert(lvl, $"[{f.Category}] {f.Headline}", f.Detail, f.Advice);
                    }
                    sink.EndDetails();
                    dumpIdx++;
                }

                if (newFindings.Count > 0 || resolvedFindings.Count > 0)
                {
                    static string Trunc(string s, int mx) => s.Length <= mx ? s : s[..mx] + "…";
                    var deltaRows = new List<string[]>();
                    foreach (var f in newFindings)      deltaRows.Add(["🆕 New",       f.Category, Trunc(f.Headline, 70)]);
                    foreach (var f in resolvedFindings) deltaRows.Add(["✅ Resolved", f.Category, Trunc(f.Headline, 70)]);
                    sink.Table(["Change", "Category", "Finding"], deltaRows,
                        $"Finding changes between {baselineLabel} and {labels[^1]}");
                }
            }
        }

        // ── 2. Overall Growth Summary ──────────────────────────────────────────
        sink.Section("2. Overall Growth Summary");
        sink.Explain(
            what: "Raw metric values for every tracked signal across all dump snapshots, with trend arrows.",
            why:  "Growth that does not reverse between dumps is the clearest signal of a leak or accumulation problem. " +
                  "This table shows the full picture across all captured snapshots rather than just baseline vs. latest.",
            bullets:
            [
                "Metrics that monotonically increase across ALL dumps → strong accumulation/leak signal",
                "Metrics that peak and then stabilize → may indicate a workload spike that resolved",
                "LOH growing while fragmentation is also high → LOH retention causing allocation pressure",
                "Event subscriber count growing → event leak accumulating across the capture window",
                "Finalizer queue growing with heap also growing → IDisposable violations causing retention",
            ]);
        var growthCols = new[] { "Metric" }.Concat(labels).Append($"Trend ({baselineLabel}→{labels[^1]})").ToArray();
        var growthRows = new List<string[]>();

        void AddRow(string lbl2, Func<DumpSnapshot, double> sel, Func<DumpSnapshot, string> fmt, bool higherIsBad = true)
        {
            var vals = snaps.Select(s => fmt(s)).ToArray();
            growthRows.Add([lbl2, .. vals, Trend(sel(s0), sel(sN), higherIsBad)]);
        }

        long SohBytes(DumpSnapshot s) => s.TotalHeapBytes - s.LohBytes - s.PohBytes - s.FrozenBytes;

        AddRow("Total Objects",    s => s.TotalObjectCount,         s => s.TotalObjectCount.ToString("N0"));
        AddRow("Heap — SOH",       s => SohBytes(s),                s => DumpHelpers.FormatSize(SohBytes(s)));
        AddRow("Heap — LOH",       s => s.LohBytes,                 s => DumpHelpers.FormatSize(s.LohBytes));
        AddRow("  LOH Live",        s => s.LohLiveBytes,             s => DumpHelpers.FormatSize(s.LohLiveBytes));
        AddRow("  LOH Free",        s => s.LohFreeBytes,             s => DumpHelpers.FormatSize(s.LohFreeBytes));
        AddRow("  LOH Frag %",      s => s.LohFragmentationPct,      s => $"{s.LohFragmentationPct:F1}%");
        AddRow("Heap — Total",     s => s.TotalHeapBytes,           s => DumpHelpers.FormatSize(s.TotalHeapBytes));
        AddRow("Fragmentation",    s => s.HeapFreeBytes,            s => $"{s.FragmentationPct:F1}%  ({DumpHelpers.FormatSize(s.HeapFreeBytes)} free)");
        AddRow("LOH Object Count", s => s.LohObjectCount,           s => s.LohObjectCount.ToString("N0"));
        AddRow("Finalize Queue",   s => s.FinalizerQueueDepth,      s => s.FinalizerQueueDepth.ToString("N0"));
        AddRow("Unique Strings",   s => s.UniqueStringCount,        s => s.UniqueStringCount.ToString("N0"));
        AddRow("Total String Mem", s => s.StringTotalBytes,         s => DumpHelpers.FormatSize(s.StringTotalBytes));
        AddRow("Event Instances",  s => s.EventSubscriberTotal,     s => s.EventSubscriberTotal.ToString("N0"));
        AddRow("Event Types",      s => s.EventLeakFieldCount,      s => s.EventLeakFieldCount.ToString("N0"));
        AddRow("Handles — Pinned", s => s.PinnedHandleCount,        s => s.PinnedHandleCount.ToString("N0"));
        AddRow("Handles — Strong", s => s.StrongHandleCount,        s => s.StrongHandleCount.ToString("N0"));
        AddRow("Handles — Weak",   s => s.WeakHandleCount,          s => s.WeakHandleCount.ToString("N0"), higherIsBad: false);
        AddRow("Modules (App)",    s => s.AppModuleCount,           s => $"{s.AppModuleCount} / {s.ModuleCount}");
        sink.Table(growthCols, growthRows);

        // ── 3. Thread & Application Pressure ──────────────────────────────────
        sink.Section("3. Thread & Application Pressure");
        sink.Explain(
            what: "Thread counts, thread pool state, async backlog, and execution-level resource usage across dumps.",
            why:  "High thread counts, blocked threads, zero idle workers, or a growing async backlog indicate " +
                  "the application is under execution pressure. This directly affects request throughput and latency.",
            bullets:
            [
                "Async backlog growing → downstream I/O bottleneck or thread pool starvation",
                "Idle workers decreasing → thread pool consumption increasing, risk of starvation",
                "Blocked threads increasing → growing lock contention or slow I/O operations",
                "Timer count growing → Timer objects not being disposed (run 'timer-leaks <dump>')",
                "DB connections growing → connection pool pressure or connections not being returned",
            ],
            impact: "Thread pool starvation causes async operations to queue indefinitely. " +
                    "Under starvation the application may appear completely unresponsive despite normal CPU utilization.");
        {
            string[] MRow(string lbl2, Func<DumpSnapshot, string> fmt, string trend)
                => [lbl2, .. snaps.Select(fmt), trend];

            sink.Table(
                [.. new[] { "Metric" }.Concat(labels), $"Trend ({baselineLabel}→{labels[^1]})"],
                new List<string[]>
                {
                    MRow("Threads (Total)",         s => s.ThreadCount.ToString("N0"),            Trend(s0.ThreadCount, sN.ThreadCount)),
                    MRow("  Alive",                 s => s.AliveThreadCount.ToString("N0"),       Trend(s0.AliveThreadCount, sN.AliveThreadCount)),
                    MRow("  Blocked",               s => s.BlockedThreadCount.ToString("N0"),     Trend(s0.BlockedThreadCount, sN.BlockedThreadCount)),
                    MRow("  With Active Exception", s => s.ExceptionThreadCount.ToString("N0"),   Trend(s0.ExceptionThreadCount, sN.ExceptionThreadCount)),
                    MRow("Thread Pool — Active",    s => s.TpActiveWorkers.ToString("N0"),        Trend(s0.TpActiveWorkers, sN.TpActiveWorkers)),
                    MRow("Thread Pool — Idle",      s => s.TpIdleWorkers.ToString("N0"),          Trend(s0.TpIdleWorkers, sN.TpIdleWorkers, higherIsBad: false)),
                    MRow("Async Backlog",           s => s.AsyncBacklogTotal.ToString("N0"),      Trend(s0.AsyncBacklogTotal, sN.AsyncBacklogTotal)),
                    MRow("Timer Objects",           s => s.TimerCount.ToString("N0"),             Trend(s0.TimerCount, sN.TimerCount)),
                    MRow("WCF Objects",             s => s.WcfObjectCount.ToString("N0"),         Trend(s0.WcfObjectCount, sN.WcfObjectCount)),
                    MRow("  WCF Faulted",           s => s.WcfFaultedCount.ToString("N0"),        Trend(s0.WcfFaultedCount, sN.WcfFaultedCount)),
                    MRow("DB Connections",          s => s.ConnectionCount.ToString("N0"),        Trend(s0.ConnectionCount, sN.ConnectionCount)),
                },
                "Thread and application-level object counts across dumps");

            sink.Table(
                [.. new[] { "Generation" }.Concat(labels), $"Trend ({baselineLabel}→{labels[^1]})"],
                new List<string[]>
                {
                    MRow("Gen 0",          s => DumpHelpers.FormatSize(s.Gen0Bytes),       Trend(s0.Gen0Bytes, sN.Gen0Bytes)),
                    MRow("Gen 1",          s => DumpHelpers.FormatSize(s.Gen1Bytes),       Trend(s0.Gen1Bytes, sN.Gen1Bytes)),
                    MRow("Gen 2",          s => DumpHelpers.FormatSize(s.Gen2Bytes),       Trend(s0.Gen2Bytes, sN.Gen2Bytes)),
                    MRow("LOH",            s => DumpHelpers.FormatSize(s.LohBytes),        Trend(s0.LohBytes, sN.LohBytes)),
                    MRow("POH",            s => DumpHelpers.FormatSize(s.PohBytes),        Trend(s0.PohBytes, sN.PohBytes)),
                    MRow("Fragmentation (free)", s => $"{s.FragmentationPct:F1}%  ({DumpHelpers.FormatSize(s.HeapFreeBytes)} free)",
                         Trend(s0.HeapFreeBytes, sN.HeapFreeBytes)),
                },
                "GC generation sizes and fragmentation across dumps");

            var allExTypes = snaps.SelectMany(s => s.ExceptionCounts.Select(e => e.Name)).Distinct().ToList();
            if (allExTypes.Count > 0)
            {
                var exCols = new[] { "Exception Type" }.Concat(labels).ToArray();
                var exRows = allExTypes
                    .Select(t =>
                    {
                        var counts = snaps.Select(s =>
                        {
                            var e = s.ExceptionCounts.FirstOrDefault(x => x.Name == t);
                            return e == default(NameCount) ? "—" : e.Count.ToString("N0");
                        }).ToArray();
                        return (string[])[t, .. counts];
                    })
                    .OrderByDescending(r =>
                    {
                        int max = 0;
                        for (int i = 1; i < r.Length; i++)
                            if (int.TryParse(r[i].Replace(",", ""), out int v) && v > max) max = v;
                        return max;
                    })
                    .Take(10)
                    .ToList();
                sink.Table(exCols, exRows, "Top exception types across dumps");
            }

            var allAsyncMethods = snaps.SelectMany(s => s.TopAsyncMethods.Select(m => m.Name)).Distinct().ToList();
            if (allAsyncMethods.Count > 0)
            {
                var aCols = new[] { "Async Method" }.Concat(labels).ToArray();
                var aRows = allAsyncMethods
                    .Select(m =>
                    {
                        var counts = snaps.Select(s =>
                        {
                            var am = s.TopAsyncMethods.FirstOrDefault(x => x.Name == m);
                            return am == default(NameCount) ? "—" : am.Count.ToString("N0");
                        }).ToArray();
                        return (string[])[m, .. counts];
                    })
                    .OrderByDescending(r =>
                    {
                        int max = 0;
                        for (int i = 1; i < r.Length; i++)
                            if (int.TryParse(r[i].Replace(",", ""), out int v) && v > max) max = v;
                        return max;
                    })
                    .Take(10)
                    .ToList();
                sink.Table(aCols, aRows, "Top async state machine methods across dumps");
            }
        }

        // ── 4. Event Leak Analysis ────────────────────────────────────────────
        sink.Section("4. Event Leak Analysis");
        sink.Explain(
            what: "Event handler subscription counts tracked per event field across all dumps. " +
                  "An event leak occurs when subscribers remain referenced by publishers after the subscriber should have been released.",
            why:  "Because the publisher's event field holds delegate references, the garbage collector cannot reclaim " +
                  "any object in the subscriber's graph. Growing subscriber counts across dumps prove accumulation — " +
                  "the object graph is growing in direct proportion to the subscription count.",
            bullets:
            [
                "Subscriber count growing across ALL dumps → subscriptions accumulating, no unsubscription happening",
                "Count stable but critically high → issue is persistent, not recovering",
                "Single field with very high count → likely a singleton or static publisher",
            ],
            impact: "Event leaks cause silent, unbounded memory growth. The subscriber objects appear 'in use' " +
                    "from the GC's perspective and will never be collected until the publisher is also collected.",
            action: "Run 'event-analysis <dump>' for the latest dump to see subscriber types and static publisher detection. " +
                    "Search call sites for '+=' without corresponding '-=' in Dispose().");
        {
            sink.Table(
                ["Dump", "Total Instances", "Distinct Event Types", "Max on Single Field"],
                snaps.Select((s, i) => new[]
                {
                    labels[i],
                    s.EventSubscriberTotal > 0 ? s.EventSubscriberTotal.ToString("N0") : "—",
                    s.EventLeakFieldCount  > 0 ? s.EventLeakFieldCount.ToString("N0")  : "—",
                    s.EventLeakMaxOnField  > 0 ? s.EventLeakMaxOnField.ToString("N0")  : "—",
                }).ToList());

            var allFields = snaps
                .SelectMany(s => s.TopEventLeaks)
                .Select(e => (e.PublisherType, e.FieldName))
                .Distinct()
                .ToList();

            if (ignoreEventTypes is { Count: > 0 })
            {
                int before = allFields.Count;
                allFields = allFields
                    .Where(f => !ignoreEventTypes.Any(ig =>
                        f.PublisherType.Contains(ig, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                int removed = before - allFields.Count;
                if (removed > 0)
                    sink.Text($"Filtered out {removed} event field(s) matching: {string.Join(", ", ignoreEventTypes)}");
            }

            if (allFields.Count > 0)
            {
                var eventCols = new[] { "Event Type / Field" }.Concat(labels).ToArray();
                var eventRows = allFields
                    .Select(key =>
                    {
                        var perDump = snaps.Select(s =>
                        {
                            var stat = s.TopEventLeaks
                                .FirstOrDefault(e => e.PublisherType == key.PublisherType && e.FieldName == key.FieldName);
                            return stat is null ? "—" : stat.Subscribers.ToString("N0");
                        }).ToArray();
                        return (string[])[$"{key.PublisherType}.{key.FieldName}", .. perDump];
                    })
                    .OrderByDescending(r =>
                    {
                        int max = 0;
                        for (int i = 1; i < r.Length; i++)
                            if (int.TryParse(r[i].Replace(",", ""), out int v) && v > max) max = v;
                        return max;
                    })
                    .Take(15)
                    .ToList();
                sink.Table(eventCols, eventRows, "Top event fields across all dumps");
            }
        }

        // ── 5. Finalize Queue Detail ──────────────────────────────────────────
        sink.Section("5. Finalize Queue Detail");
        sink.Explain(
            what: "The number and types of objects waiting in the GC's finalization queue across dumps. " +
                  "Objects enter the queue when they implement a finalizer (Finalize()/destructor) and the GC " +
                  "has determined they are unreachable but have not yet been cleaned up.",
            why:  "Objects in the finalizer queue cannot be reclaimed immediately — they must be processed by the " +
                  "single finalizer thread first. A growing queue delays memory reclamation and can indicate " +
                  "that IDisposable is not being called before objects are abandoned.",
            bullets:
            [
                "Queue growing across all dumps → continuous creation of finalizable objects without Dispose()",
                "Queue stable but elevated → a persistent IDisposable violation, not self-recovering",
                "Queue decreasing → cleanup behavior improved, but if still > 0 the issue is not fully resolved",
                "SafeHandle/CriticalFinalizerObject types → native handle release is being delayed",
            ],
            impact: "A large finalizer queue increases GC pause times, delays native handle release, and can cause " +
                    "cascading resource exhaustion (handle limits, connection pool exhaustion).",
            action: "Run 'finalizer-queue <dump>' for the latest dump to identify which types are in the queue. " +
                    "Audit IDisposable usage — wrap all IDisposable objects in 'using' statements.");
        {
            sink.Table(
                ["Dump", "Total in Queue"],
                snaps.Select((s, i) => new[] { labels[i], s.FinalizerQueueDepth.ToString("N0") }).ToList());

            var allFinTypes = snaps.SelectMany(s => s.TopFinalizerTypes.Select(t => t.Name)).Distinct().ToList();
            if (allFinTypes.Count > 0)
            {
                var finCols = new[] { "Type" }.Concat(labels).ToArray();
                var finRows = allFinTypes
                    .Select(typeName =>
                    {
                        var counts = snaps.Select(s =>
                        {
                            var t = s.TopFinalizerTypes.FirstOrDefault(x => x.Name == typeName);
                            return t == default(NameCount) ? "—" : t.Count.ToString("N0");
                        }).ToArray();
                        return (string[])[typeName, .. counts];
                    })
                    .OrderByDescending(r =>
                    {
                        int max = 0;
                        for (int i = 1; i < r.Length; i++)
                            if (int.TryParse(r[i].Replace(",", ""), out int v) && v > max) max = v;
                        return max;
                    })
                    .Take(15)
                    .ToList();
                sink.Table(finCols, finRows, "Top types by peak count");
            }
        }

        // ── 6. Highly Referenced Objects ──────────────────────────────────────
        sink.Section("6. Highly Referenced Objects");
        sink.Explain(
            what: "Types with the highest instance counts across dumps, used as a proxy for highly-referenced objects. " +
                  "Types with many instances are often roots of large retained object graphs.",
            why:  "Types accumulating across dumps without decreasing are the primary leak suspects. " +
                  "High instance counts indicate many objects of that type are alive simultaneously — " +
                  "either intentionally (legitimate caching) or accidentally (leak).",
            bullets:
            [
                "Type count growing monotonically → strong leak suspect — investigate GC roots",
                "System.String at high count → string duplication or retained string collections",
                "Task, CancellationTokenSource, HttpClient → async resource leak patterns",
                "Your own service/model types accumulating → retained in static collections or event handlers",
            ],
            action: "Run 'gc-roots <dump> --type <TypeName>' for the top accumulating types to trace why instances are retained.");
        sink.Text("Reference graph analysis requires a live debugging session or WinDbg/SOS.");
        sink.Text("Use: !gcroot <address>  or  DumpDetective gc-roots <dump> to inspect specific objects.");
        sink.Text("Top types by instance count (proxy for high-fanout objects):");
        {
            var allTopTypes = snaps.SelectMany(s => s.TopTypes.Select(t => t.Name)).Distinct().ToList();

            var typeCols = new[] { "Type" }.Concat(labels.Select(l => $"{l} Count")).ToArray();
            var typeRows = allTopTypes
                .Select(name =>
                {
                    var counts = snaps.Select(s =>
                    {
                        var t = s.TopTypes.FirstOrDefault(x => x.Name == name);
                        return t is null ? "—" : t.Count.ToString("N0");
                    }).ToArray();
                    return (string[])[name, .. counts];
                })
                .OrderByDescending(r =>
                {
                    long max = 0;
                    for (int i = 1; i < r.Length; i++)
                        if (long.TryParse(r[i].Replace(",", ""), out long v) && v > max) max = v;
                    return max;
                })
                .Take(15)
                .ToList();
            if (typeRows.Count > 0)
                sink.Table(typeCols, typeRows, "Top 15 types by peak instance count across dumps");

            var typeSizeCols = new[] { "Type" }.Concat(labels.Select(l => $"{l} Size")).Append("Trend").ToArray();
            var typeSizeRows = allTopTypes
                .Select(name =>
                {
                    var sizes = snaps.Select(s =>
                    {
                        var t = s.TopTypes.FirstOrDefault(x => x.Name == name);
                        return (raw: t?.TotalBytes ?? 0L, fmt: t is null ? "—" : DumpHelpers.FormatSize(t.TotalBytes));
                    }).ToArray();
                    string trend = Trend(sizes[0].raw, sizes[^1].raw);
                    return (row: (string[])[name, .. sizes.Select(x => x.fmt), trend], peak: sizes.Max(x => x.raw));
                })
                .OrderByDescending(x => x.peak)
                .Take(15)
                .Select(x => x.row)
                .ToList();
            if (typeSizeRows.Count > 0)
                sink.Table(typeSizeCols, typeSizeRows, "Top 15 types by peak total size across dumps");
        }

        // ── 7. Rooted Objects Analysis ────────────────────────────────────────
        sink.Section("7. Rooted Objects Analysis");
        {
            sink.Table(
                ["Dump", "Strong", "Pinned", "Weak", "Total"],
                snaps.Select((s, i) => new[]
                {
                    labels[i],
                    s.StrongHandleCount.ToString("N0"),
                    s.PinnedHandleCount.ToString("N0"),
                    s.WeakHandleCount.ToString("N0"),
                    s.TotalHandleCount.ToString("N0"),
                }).ToList(), "Handle counts per dump");

            var allRootKeys = snaps
                .SelectMany(s => s.TopRootedTypes.Select(r => (r.HandleKind, r.TypeName)))
                .Distinct()
                .ToList();

            if (allRootKeys.Count > 0)
            {
                var rootCols = new[] { "Root Type (Handle Kind)" }.Concat(labels).ToArray();
                var rootRows = allRootKeys
                    .Select(key =>
                    {
                        var counts = snaps.Select(s =>
                        {
                            var r = s.TopRootedTypes.FirstOrDefault(x => x.HandleKind == key.HandleKind && x.TypeName == key.TypeName);
                            return r is null ? "—" : $"{r.Count:N0} / {DumpHelpers.FormatSize(r.TotalBytes)}";
                        }).ToArray();
                        return (string[])[$"{key.TypeName} ({key.HandleKind})", .. counts];
                    })
                    .OrderByDescending(r =>
                    {
                        int max = 0;
                        for (int i = 1; i < r.Length; i++)
                        {
                            var cell  = r[i];
                            var slash = cell.IndexOf('/');
                            var numPart = slash >= 0 ? cell[..slash].Trim() : cell;
                            if (int.TryParse(numPart.Replace(",", ""), out int v) && v > max) max = v;
                        }
                        return max;
                    })
                    .Take(15)
                    .ToList();
                sink.Table(rootCols, rootRows, "Top rooted types by peak count  (count / total size)");
            }
        }

        // ── 8. Duplicate String Analysis ──────────────────────────────────────
        sink.Section("8. Duplicate String Analysis");
        if (!full)
        {
            sink.Text("Not collected — re-run with --full to include string duplicate detail.");
        }
        else
        {
            sink.Table(
                ["Dump", "Unique Strings", "Duplicate Groups", "Wasted Memory", "Total String Mem"],
                snaps.Select((s, i) => new[]
                {
                    labels[i],
                    s.UniqueStringCount.ToString("N0"),
                    s.StringDuplicateGroups.ToString("N0"),
                    DumpHelpers.FormatSize(s.StringWastedBytes),
                    DumpHelpers.FormatSize(s.StringTotalBytes),
                }).ToList());

            var allStringVals = snaps.SelectMany(s => s.TopStringDuplicates.Select(d => d.Value)).Distinct().ToList();
            if (allStringVals.Count > 0)
            {
                var strCols = new[] { "String Value" }
                    .Concat(labels.Select(l => $"{l} Count"))
                    .Concat(labels.Select(l => $"{l} Wasted"))
                    .ToArray();

                var strRows = allStringVals
                    .Select(val =>
                    {
                        var counts = snaps.Select(s =>
                        {
                            var d = s.TopStringDuplicates.FirstOrDefault(x => x.Value == val);
                            return d is null ? "—" : d.Count.ToString("N0");
                        }).ToArray();
                        var wasted = snaps.Select(s =>
                        {
                            var d = s.TopStringDuplicates.FirstOrDefault(x => x.Value == val);
                            return d is null ? "—" : DumpHelpers.FormatSize(d.WastedBytes);
                        }).ToArray();
                        string display = val.Length > 60 ? val[..60] + "…" : val;
                        display = display.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
                        return (string[])[$"\"{display}\"", .. counts, .. wasted];
                    })
                    .OrderByDescending(r =>
                    {
                        int max = 0;
                        for (int i = 1; i <= snaps.Count; i++)
                            if (int.TryParse(r[i].Replace(",", ""), out int v) && v > max) max = v;
                        return max;
                    })
                    .Take(15)
                    .ToList();
                sink.Table(strCols, strRows, "Top duplicated strings across all dumps");
            }
        }

        // ── 9. Memory Leak Analysis ────────────────────────────────────────────
        sink.Section("9. Memory Leak Analysis");
        {
            var leakCols = new[] { "Metric" }.Concat(labels).Append($"Trend ({baselineLabel}→{labels[^1]})").ToArray();
            var leakRows = new List<string[]>();

            double Gen2Pct(DumpSnapshot s) => s.TotalHeapBytes > 0 ? s.Gen2Bytes * 100.0 / s.TotalHeapBytes : 0;

            leakRows.Add(["Gen2 % of Heap",
                .. snaps.Select(s => $"{Gen2Pct(s):F1}%  ({DumpHelpers.FormatSize(s.Gen2Bytes)})"),
                Trend(Gen2Pct(s0), Gen2Pct(sN))]);
            leakRows.Add(["LOH Size",
                .. snaps.Select(s => DumpHelpers.FormatSize(s.LohBytes)),
                Trend(s0.LohBytes, sN.LohBytes)]);
            leakRows.Add(["Total Objects",
                .. snaps.Select(s => s.TotalObjectCount.ToString("N0")),
                Trend(s0.TotalObjectCount, sN.TotalObjectCount)]);
            leakRows.Add(["System.String Count",
                .. snaps.Select(s =>
                {
                    var t = s.TopTypes.FirstOrDefault(x => x.Name == "System.String");
                    return t is null ? "—" : t.Count.ToString("N0");
                }),
                Trend(
                    s0.TopTypes.FirstOrDefault(x => x.Name == "System.String")?.Count ?? 0,
                    sN.TopTypes.FirstOrDefault(x => x.Name == "System.String")?.Count ?? 0)]);
            leakRows.Add(["System.String Memory",
                .. snaps.Select(s =>
                {
                    var t = s.TopTypes.FirstOrDefault(x => x.Name == "System.String");
                    return t is null ? "—" : DumpHelpers.FormatSize(t.TotalBytes);
                }),
                Trend(
                    s0.TopTypes.FirstOrDefault(x => x.Name == "System.String")?.TotalBytes ?? 0,
                    sN.TopTypes.FirstOrDefault(x => x.Name == "System.String")?.TotalBytes ?? 0)]);

            sink.Table(leakCols, leakRows, "Memory leak key indicators across dumps");

            var allTypeNames = snaps.SelectMany(s => s.TopTypes.Select(t => t.Name)).Distinct().ToList();
            double gen2PctLatest = Gen2Pct(sN);
            double gen2PctBase   = Gen2Pct(s0);
            double gen2Delta     = gen2PctLatest - gen2PctBase;

            var newOrExplosive = allTypeNames
                .Select(name =>
                {
                    long baseCount   = s0.TopTypes.FirstOrDefault(x => x.Name == name)?.Count ?? 0;
                    long latestCount = sN.TopTypes.FirstOrDefault(x => x.Name == name)?.Count ?? 0;
                    return (name, baseCount, latestCount);
                })
                .Where(x => x.latestCount >= 500 &&
                            (x.baseCount == 0 ||
                             (x.baseCount > 0 && (x.latestCount - x.baseCount) * 100.0 / x.baseCount > 50)))
                .OrderByDescending(x => x.latestCount)
                .Take(5)
                .ToList();

            if (gen2PctLatest > 50)
                sink.Alert(AlertLevel.Critical,
                    $"Gen2 holds {gen2PctLatest:F1}% of heap in latest dump ({labels[^1]})",
                    detail: "Objects are accumulating across GC generations — strong managed memory leak signal.",
                    advice: $"Run: memory-leak <dump>  on {labels[^1]} to identify suspect types and GC root chains.");
            else if (gen2Delta > 10)
                sink.Alert(AlertLevel.Warning,
                    $"Gen2 grew from {gen2PctBase:F1}% ({baselineLabel}) to {gen2PctLatest:F1}% ({labels[^1]})",
                    detail: $"+{gen2Delta:F1} percentage points across the analysis window.",
                    advice: $"Run: memory-leak <dump>  on {labels[^1]} to identify accumulating types.");
            else if (newOrExplosive.Any(x => x.baseCount == 0))
                sink.Alert(AlertLevel.Critical,
                    $"New high-count types appeared since {baselineLabel} that were absent before",
                    detail: string.Join("\n", newOrExplosive.Where(x => x.baseCount == 0).Select(x => $"{x.name}  (0 → {x.latestCount:N0})")),
                    advice: $"Run: memory-leak <dump>  on {labels[^1]} to trace GC root chains for these types.");
            else if (newOrExplosive.Any(x => x.baseCount > 0))
                sink.Alert(AlertLevel.Warning,
                    $"Types with >50% instance count growth between {baselineLabel} and {labels[^1]}",
                    detail: string.Join("\n", newOrExplosive.Where(x => x.baseCount > 0).Select(x => $"{x.name}  ({x.baseCount:N0} → {x.latestCount:N0})")),
                    advice: $"Run: memory-leak <dump>  on {labels[^1]} to identify the retaining root.");
            else
                sink.Alert(AlertLevel.Info,
                    $"Gen2 at {gen2PctLatest:F1}% in latest dump ({labels[^1]}) — no strong leak signal from generation data alone.",
                    advice: $"Run: memory-leak <dump>  on {labels[^1]} for deeper analysis including GC root chains.");

            var topByCount = allTypeNames
                .Select(name =>
                {
                    var perDump = snaps.Select(s =>
                    {
                        var t = s.TopTypes.FirstOrDefault(x => x.Name == name);
                        return (raw: t?.Count ?? 0L, fmt: t is null ? "—" : t.Count.ToString("N0"));
                    }).ToArray();
                    long bLine  = perDump[baselineIndex].raw;
                    long latest = perDump[^1].raw;
                    return (row: (string[])[name, .. perDump.Select(x => x.fmt), Trend(bLine, latest)], peak: perDump.Max(x => x.raw));
                })
                .OrderByDescending(x => x.peak)
                .Take(15)
                .Select(x => x.row)
                .ToList();

            if (topByCount.Count > 0)
                sink.Table(
                    [.. new[] { "Type" }.Concat(labels), "Trend"],
                    topByCount,
                    "Top 15 types by peak instance count — types growing over time are strong leak suspects. " +
                    "Run 'memory-leak <dump>' on the latest dump for GC root chains.");
        }
    }

    // ── Diagnosis Summary ─────────────────────────────────────────────────────

    private static void RenderDiagnosisSummary(
        List<DumpSnapshot> snaps, DumpSnapshot s0, DumpSnapshot sN,
        string[] labels, string baselineLabel, IRenderSink sink)
    {
        static string SevLabel(FindingSeverity s) => s switch
        {
            FindingSeverity.Critical => "CRITICAL",
            FindingSeverity.Warning  => "WARNING",
            _                        => "INFO",
        };

        static string Fs(long b)  => DumpHelpers.FormatSize(b);
        static string N(double v) => v >= 1_000_000 ? $"{v / 1_000_000.0:F1}M"
                                   : v >= 1_000      ? $"{v / 1_000.0:F1}K"
                                   : v.ToString("N0");

        string Join(Func<DumpSnapshot, string> sel) =>
            string.Join("  \u2192  ", snaps.Select((s, i) => $"{labels[i]}: {sel(s)}"));

        static string ExtractTypeName(string headline)
        {
            const string prefix = "High instance count: ";
            int start = headline.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return headline;
            string rest = headline[(start + prefix.Length)..];
            int cross = rest.IndexOf('\u00d7');
            return cross >= 0 ? rest[..cross].Trim() : rest.Trim();
        }

        static string TopicKey(Finding f)
        {
            string h = f.Headline.ToLowerInvariant();
            return f.Category switch
            {
                "Memory" when h.Contains("finalizer")                               => "Memory|finalizer",
                "Memory" when h.Contains("fragment")                                => "Memory|fragmentation",
                "Memory" when h.Contains("loh")                                     => "Memory|loh",
                "Memory" when h.Contains("pinned")                                  => "Memory|pinned",
                "Memory" when h.Contains("string")                                  => "Memory|string",
                "Memory"                                                             => "Memory|heap",
                "Memory Leak" when h.Contains("gen2")                               => "MemoryLeak|gen2",
                "Memory Leak"                                                        => $"MemoryLeak|{ExtractTypeName(f.Headline)}",
                "Async"                                                              => "Async|backlog",
                "Threading" when h.Contains("saturated") || h.Contains("capacity") => "Threading|tpsat",
                "Threading"                                                          => "Threading|blocked",
                "Connections"                                                        => "Connections|db",
                "Leaks"     when h.Contains("event")                                => "Leaks|event",
                "Leaks"                                                              => "Leaks|timer",
                "Exceptions"                                                         => "Exceptions|active",
                "WCF"                                                                => "WCF|faulted",
                _                                                                    => $"{f.Category}|{f.Headline}",
            };
        }

        static string CanonicalTitle(string key) => key switch
        {
            "Memory|heap"           => "Heap Size",
            "Memory|loh"            => "LOH (Large Object Heap) Growth",
            "Memory|fragmentation"  => "Heap Fragmentation",
            "Memory|finalizer"      => "Finalizer Queue Accumulating",
            "Memory|pinned"         => "Pinned GC Handles",
            "Memory|string"         => "String Duplication Waste",
            "MemoryLeak|gen2"       => "Gen2 Memory Leak",
            "Async|backlog"         => "Async Continuation Backlog",
            "Threading|tpsat"       => "Thread Pool Saturation",
            "Threading|blocked"     => "Blocked Threads",
            "Connections|db"        => "DB Connection Leak",
            "Leaks|event"           => "Event Handler Leak",
            "Leaks|timer"           => "Timer Leak",
            "Exceptions|active"     => "Active Thread Exceptions",
            "WCF|faulted"           => "WCF Faulted Channels",
            _ when key.StartsWith("MemoryLeak|") => $"High Instance Count: {key["MemoryLeak|".Length..]}",
            _ => key.Contains('|') ? key[(key.IndexOf('|') + 1)..] : key,
        };

        static string FallbackAdvice(string key) => key switch
        {
            "Memory|heap"          => "Investigate large object allocations. Consider increasing GC LOH threshold or reviewing allocation patterns.",
            "Memory|loh"           => "LOH is not compacted by default. Avoid frequent large allocations. Consider GCSettings.LargeObjectHeapCompactionMode.",
            "Memory|fragmentation" => "High fragmentation reduces allocation efficiency. Consider running GC.Collect(2, GCCollectionMode.Forced, true, true) during low-traffic periods.",
            "Memory|finalizer"     => "Ensure IDisposable is implemented and Dispose() is called. Use 'using' statements to prevent finalizer queue buildup.",
            "Memory|pinned"        => "Reduce GCHandle.Alloc(Pinned) usage. Prefer Memory<T>/Span<T> for interop. Unpin handles as soon as the native call completes.",
            "Memory|string"        => "Intern repeated strings with string.Intern() or consolidate via a shared dictionary/lookup table.",
            "MemoryLeak|gen2"      => "Identify types accumulating in Gen2. Run: gc-roots <dump> to trace retaining roots.",
            "Async|backlog"        => "A downstream dependency is blocking async continuations. Profile the thread pool and check for synchronous blocking calls.",
            "Threading|tpsat"      => "Thread pool is saturated. Reduce blocking calls inside async methods. Consider increasing thread pool min threads.",
            "Threading|blocked"    => "Threads are blocked waiting on locks or I/O. Run: deadlock-detection <dump> to identify contention chains.",
            "Connections|db"       => "SQL connections are not being returned to the pool. Ensure SqlConnection is wrapped in using or explicitly disposed.",
            "Leaks|event"          => "Unsubscribe event handlers when objects are disposed. Use weak event patterns or WeakReference<T> for publisher/subscriber decoupling.",
            "Leaks|timer"          => "Dispose System.Threading.Timer and System.Timers.Timer instances when no longer needed. Leaked timers keep closures alive.",
            "Exceptions|active"    => "Frequent active exceptions degrade throughput. Review catch/rethrow patterns and eliminate exception-as-control-flow usage.",
            "WCF|faulted"          => "Faulted WCF channels must be aborted (not closed). Call channel.Abort() in the catch block before recreating the channel.",
            _ when key.StartsWith("MemoryLeak|") => $"Run: memory-leak <dump> to trace GC root chains for {key["MemoryLeak|".Length..]}.",
            _ => "Investigate using the relevant per-dump analyzer command.",
        };

        string Evidence(string key)
        {
            if (key.StartsWith("MemoryLeak|") && key != "MemoryLeak|gen2")
            {
                string typeName = key["MemoryLeak|".Length..];
                return Join(s =>
                {
                    var t = s.TopTypes.FirstOrDefault(x => x.Name == typeName);
                    return t is null ? "0" : $"{N(t.Count)}  ({Fs(t.TotalBytes)})";
                });
            }
            return key switch
            {
                "Memory|heap"          => Join(s => Fs(s.TotalHeapBytes)),
                "Memory|loh"           => Join(s => Fs(s.LohBytes)),
                "Memory|fragmentation" => Join(s => $"{s.FragmentationPct:F1}%  ({Fs(s.HeapFreeBytes)} free)"),
                "Memory|finalizer"     => Join(s => N(s.FinalizerQueueDepth)),
                "Memory|pinned"        => Join(s => N(s.PinnedHandleCount)),
                "Memory|string"        => Join(s => Fs(s.StringWastedBytes)),
                "MemoryLeak|gen2"      => Join(s => { double p = s.TotalHeapBytes > 0 ? s.Gen2Bytes * 100.0 / s.TotalHeapBytes : 0; return $"Gen2 {p:F1}%  ({Fs(s.Gen2Bytes)})"; }),
                "Async|backlog"        => Join(s => $"{N(s.AsyncBacklogTotal)} continuations"),
                "Threading|tpsat"      => Join(s => $"{s.TpActiveWorkers}/{s.TpMaxWorkers} workers"),
                "Threading|blocked"    => Join(s => $"{N(s.BlockedThreadCount)} blocked"),
                "Connections|db"       => Join(s => $"{N(s.ConnectionCount)} connections"),
                "Leaks|event"          => Join(s => $"{N(s.EventSubscriberTotal)} subscribers, {s.EventLeakFieldCount} fields"),
                "Leaks|timer"          => Join(s => $"{N(s.TimerCount)} timers"),
                "Exceptions|active"    => Join(s => $"{N(s.ExceptionThreadCount)} threads"),
                "WCF|faulted"          => Join(s => $"{N(s.WcfFaultedCount)} faulted"),
                _                      => "\u2014",
            };
        }

        var dedupedRows = snaps
            .SelectMany(s => s.Findings)
            .Where(f => f.Severity != FindingSeverity.Info)
            .GroupBy(f => TopicKey(f))
            .Select(g =>
            {
                var best = g.OrderByDescending(f => (int)f.Severity).First();
                return (key: g.Key, severity: best.Severity, category: best.Category,
                        title: CanonicalTitle(g.Key), advice: best.Advice ?? FallbackAdvice(g.Key));
            })
            .OrderBy(r => r.severity == FindingSeverity.Critical ? 0 : 1)
            .ThenBy(r => r.category)
            .ThenBy(r => r.title)
            .ToList();

        // ── Plain-English description ──────────────────────────────────────
        sink.Text(
            "This section summarises the health findings across all captured snapshots. " +
            "Each row in the table below tracks one diagnosed issue — severity, evidence per dump, and a recommended action. " +
            "CRITICAL rows require immediate attention; WARNING rows should be monitored.");

        int scoreDelta2 = sN.HealthScore - s0.HealthScore;
        string healthSentence = scoreDelta2 >= 10
            ? $"Overall health improved from {s0.HealthScore}/100 ({ScoreLabel(s0.HealthScore)}) to {sN.HealthScore}/100 ({ScoreLabel(sN.HealthScore)}) across the observed period."
            : scoreDelta2 <= -10
            ? $"Overall health degraded from {s0.HealthScore}/100 ({ScoreLabel(s0.HealthScore)}) to {sN.HealthScore}/100 ({ScoreLabel(sN.HealthScore)}) across the observed period."
            : $"Health score remained roughly stable at {sN.HealthScore}/100 ({ScoreLabel(sN.HealthScore)}) throughout the observed period.";
        sink.Text(healthSentence);

        var allCategories = snaps
            .SelectMany(s => s.Findings.Select(f => f.Category))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c)
            .ToList();

        foreach (var category in allCategories)
        {
            var presence = snaps
                .Select((s, i) => (label: labels[i], has: s.Findings.Any(f => f.Category == category)))
                .ToList();

            bool inFirst  = presence[0].has;
            bool inLast   = presence[^1].has;
            bool inAll    = presence.All(x => x.has);
            var  withList = presence.Where(x => x.has).Select(x => x.label).ToList();

            string sentence;
            if (inAll)
                sentence = $"{category}: issue present in all {snaps.Count} snapshots — has not resolved.";
            else if (!inFirst && inLast)
                sentence = $"{category}: newly detected in {withList[0]}, not present in the baseline.";
            else if (inFirst && !inLast)
                sentence = withList.Count == 1
                    ? $"{category}: issue was detected in {withList[0]} and appears resolved in subsequent snapshots."
                    : $"{category}: detected in {withList[0]}, last seen in {withList[^1]} — appears resolved by {presence.First(x => !x.has && labels.ToList().IndexOf(x.label) > labels.ToList().IndexOf(withList[^1])).label}.";
            else
                sentence = $"{category}: detected in {string.Join(", ", withList)}.";

            sink.Text("• " + sentence);
        }

        if (allCategories.Count == 0)
            sink.Text("• No health findings recorded across any snapshot.");

        var critTitles = dedupedRows.Where(r => r.severity == FindingSeverity.Critical).Select(r => r.title).ToList();
        var warnTitles = dedupedRows.Where(r => r.severity == FindingSeverity.Warning).Select(r => r.title).ToList();
        if (critTitles.Count > 0)
            sink.Text($"⚠ As of the latest snapshot, {critTitles.Count} finding(s) are at critical severity: {string.Join(", ", critTitles)}. Immediate investigation is recommended.");
        else if (warnTitles.Count > 0)
            sink.Text($"⚠ As of the latest snapshot, {warnTitles.Count} finding(s) are at warning severity: {string.Join(", ", warnTitles)}.");
        else
            sink.Text("✓ All findings are within acceptable thresholds in the latest snapshot.");

        sink.BlankLine();

        if (dedupedRows.Count == 0)
        {
            sink.Alert(AlertLevel.Info, "No findings above warning threshold across all dumps.",
                "All monitored signals are within acceptable thresholds.");
            return;
        }

        sink.Table(
            ["Severity", "Analyzer", "Title", "Evidence", "Recommendation"],
            dedupedRows.Select(r => new[]
            {
                SevLabel(r.severity), r.category, r.title, Evidence(r.key), r.advice,
            }).ToList(),
            $"One row per signal, deduplicated across all {snaps.Count} dump(s).  " +
            $"Evidence shows per-dump metric progression ({labels[0]} \u2192 {labels[^1]}).  " +
            "Sorted: CRITICAL first.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public static string ScoreLabel(int s) => AnalyzeReport.ScoreLabel(s);
    public static string ScoreColor(int s)  => s >= 70 ? "green" : s >= 40 ? "yellow" : "red";

    private static string Trend(double first, double last, bool higherIsBad = true)
    {
        if (first <= 0) return last > 0 ? (higherIsBad ? "↑ new" : "↓ appeared") : "~";
        if (last  <= 0) return higherIsBad ? "↓ gone" : "↑ resolved";

        double pct    = (last - first) / first * 100;
        string sign   = pct >= 0 ? "+" : "";
        string pctStr = Math.Abs(pct) >= 1_000 ? $"{sign}{pct / 1_000.0:F1}K%" : $"{sign}{pct:F0}%";

        string label;
        if (higherIsBad)
        {
            label = pct >  500 ? "↑↑ explosive growth"
                  : pct >  100 ? "↑↑ severe increase"
                  : pct >   50 ? "↑ significant increase"
                  : pct >   10 ? "↑ moderate increase"
                  : pct <  -50 ? "↓ significant drop"
                  : pct <  -10 ? "↓ improving"
                  :              "~ stable";
        }
        else
        {
            label = pct < -50 ? "↑↑ severe drop"
                  : pct < -10 ? "↑ declining"
                  : pct >  50 ? "↓ strong improvement"
                  : pct >  10 ? "↓ improving"
                  :              "~ stable";
        }

        return $"{label}  ({pctStr})";
    }
}
