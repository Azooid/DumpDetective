using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

/// <summary>
/// Renders a two-section diagnostic summary:
///  1. Executive Summary  — severity score, top issue, workload, 3-bullet action plan.
///  2. Engineering Detail — scored evidence from each analyzer, recommended next commands.
/// </summary>
public sealed class DiagnosticSummaryReport
{
    public void Render(
        WorkloadProfileData    workload,
        ConfigSmellData        config,
        ExceptionAnalysisData  exceptions,
        MemoryLeakData         leakData,
        AsyncStacksData        asyncData,
        DeadlockData           deadlocks,
        ThreadAnalysisData     threads,
        IRenderSink            sink)
    {
        // ── Compute overall incident score ────────────────────────────────
        var (topIssue, overallScore) = ComputeTopIssue(
            config, exceptions, leakData, asyncData, deadlocks, threads);

        string severityLabel = overallScore switch
        {
            >= 85 => "Critical",
            >= 65 => "High",
            >= 40 => "Medium",
            _     => "Low",
        };

        // ── Executive Summary ─────────────────────────────────────────────
        sink.Section("Executive Summary", "exec-summary");
        sink.KeyValues([
            ("Incident Severity",  $"{severityLabel}  ({overallScore}/100)"),
            ("Most Likely Issue",  topIssue),
            ("Workload",           $"{workload.Label} (confidence: {workload.Confidence}%)"),
            ("Alive Threads",      config.Config.AliveThreadCount.ToString()),
            ("Blocked Threads",    config.Config.BlockedThreadCount.ToString()),
            ("Async Backlog",      asyncData.BacklogTotal.ToString()),
            ("Deadlock Cycles",    deadlocks.ConfirmedCycles.Count.ToString()),
            ("Config Smells",      $"{config.SmellCount}"),
        ]);

        // ── Action plan (3 most important things) ─────────────────────────
        var actionPlan = BuildActionPlan(config, exceptions, leakData, asyncData, deadlocks, threads);
        if (actionPlan.Count > 0)
        {
            sink.Section("Action Plan", "action-plan");
            sink.Alert(AlertLevel.Warning,
                string.Join("\n", actionPlan.Select((s, i) => $"{i + 1}. {s}")),
                detail: "Address these items in priority order.");
        }

        // ── Fatal precursors ──────────────────────────────────────────────
        var flags = exceptions.FatalFlags;
        if (flags.HasOom || flags.HasStackOverflow || flags.HasAccessViolation || flags.HasThreadAbort)
        {
            var fatals = new List<string>();
            if (flags.HasOom)             fatals.Add("OutOfMemoryException (OOM precursor)");
            if (flags.HasStackOverflow)   fatals.Add("StackOverflowException (recursion / stack depth)");
            if (flags.HasAccessViolation) fatals.Add("AccessViolationException (memory corruption risk)");
            if (flags.HasThreadAbort)     fatals.Add("ThreadAbortException (force-abort pattern)");

            sink.Alert(AlertLevel.Critical,
                "Fatal precursor exception types detected on the heap: " + string.Join(", ", fatals),
                "These exception types indicate the process was under severe stress or may have crashed. " +
                "Investigate immediately with 'exception-analysis'.",
                "Run: DumpDetective exception-analysis <dump>");
        }

        // ── Engineering Detail ────────────────────────────────────────────
        sink.Section("Engineering Evidence", "eng-evidence");

        // Config smells
        if (config.SmellCount > 0)
        {
            var rows = config.Smells.Select(s => new string[]
            {
                s.Severity.ToString(), s.Category, s.Title, s.Score.ToString()
            }).ToList();
            sink.Table(
                ["Severity", "Category", "Issue", "Score"],
                rows,
                "Configuration smells detected from runtime state");
        }
        else
        {
            sink.Alert(AlertLevel.Info, "No configuration smells detected.", detail: null);
        }

        // Thread breakdown
        sink.Section("Thread State Breakdown", "thread-breakdown");
        sink.KeyValues([
            ("Total threads",            threads.TotalCount.ToString()),
            ("Alive",                    threads.AliveCount.ToString()),
            ("Blocked (Monitor)",        threads.MonitorBlockedCount.ToString()),
            ("Blocked (Wait/Sleep/Join)",threads.IndependentWaitCount.ToString()),
            ("Async backlog (suspended)",asyncData.BacklogTotal.ToString()),
        ]);

        // Top leak suspects
        if (leakData.CountSuspects.Count > 0)
        {
            sink.Section("Top Leak Suspects", "leak-suspects");
            var rows = leakData.CountSuspects
                .Where(s => s.LeakProbability > 0)
                .OrderByDescending(s => s.LeakProbability)
                .Take(8)
                .Select(s => new string[]
                {
                    s.Name.Length > 60 ? "…" + s.Name[^57..] : s.Name,
                    s.Count.ToString("N0"),
                    DumpHelpers.FormatSize(s.Size),
                    s.Gen,
                    $"{s.LeakProbability}%",
                })
                .ToList();
            if (rows.Count > 0)
                sink.Table(
                    ["Type", "Count", "Size", "Gen", "Leak Probability"],
                    rows,
                    "Types with highest computed leak probability. Run 'memory-leak' for root-cause chains.");
        }

        // Async dependency chains
        if (asyncData.DepChains is { Count: > 0 } chains)
        {
            sink.Section("Async Dependency Chains", "async-chains");
            var rows = chains.Take(8).Select(c => new string[]
            {
                c.Root.Length > 60 ? "…" + c.Root[^57..] : c.Root,
                c.InstanceCount.ToString(),
                c.LikelyBlockedOnIo ? "I/O" : "CPU/Other",
                string.Join(" → ", c.Chain.Take(3).Select(m =>
                {
                    int p = m.LastIndexOf('.');
                    return p > 0 ? m[(p + 1)..] : m;
                })),
            }).ToList();
            sink.Table(
                ["Class", "Instances", "Likely Blocked On", "Method Chain (truncated)"],
                rows,
                "Inferred async call chains from suspended state machines");
        }

        // Deadlock summary
        if (deadlocks.ConfirmedCycles.Count > 0)
        {
            sink.Section("Deadlock Cycles Detected", "deadlock-cycles");
            foreach (var cycle in deadlocks.ConfirmedCycles)
            {
                var threadIds = cycle.ThreadIds;
                string chain = string.Join(" → ", threadIds.Select((id, idx) =>
                {
                    string? lockType = null;
                    if (cycle.LockTypeNames is { Count: > 0 } names && idx < names.Count)
                        lockType = names[idx];
                    return lockType is not null ? $"T{id} [holds {ShortName(lockType)}]" : $"T{id}";
                }));
                sink.Alert(AlertLevel.Critical,
                    $"Deadlock: {chain}",
                    "Run 'deadlock-detection' for full wait-for graph and lock details.",
                    "Standardize lock acquisition order or use async primitives.");
            }
        }

        // Workload recommendations
        sink.Section("Recommended Follow-Up Commands", "recommended-commands");
        var cmdRows = workload.RecommendedCommands.Select(cmd =>
            new string[] { cmd, $"DumpDetective {cmd} <dump-file>" }).ToList();
        sink.Table(["Command", "Invocation"], cmdRows,
            $"Commands recommended for {workload.Label} workload based on detected signals");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (string TopIssue, int Score) ComputeTopIssue(
        ConfigSmellData        config,
        ExceptionAnalysisData  exceptions,
        MemoryLeakData         leakData,
        AsyncStacksData        asyncData,
        DeadlockData           deadlocks,
        ThreadAnalysisData     threads)
    {
        var candidates = new List<(string Issue, int Score)>();

        if (deadlocks.ConfirmedCycles.Count > 0)
            candidates.Add(($"Deadlock detected ({deadlocks.ConfirmedCycles.Count} cycle(s))", 97));

        var flags = exceptions.FatalFlags;
        if (flags.HasOom)
            candidates.Add(("OutOfMemoryException — heap exhaustion", 95));
        if (flags.HasStackOverflow)
            candidates.Add(("StackOverflowException — infinite recursion or stack too deep", 93));

        if (asyncData.BacklogTotal >= 200)
            candidates.Add(($"Async backlog: {asyncData.BacklogTotal} stuck state machines — likely sync-over-async", 90));

        if (config.SmellCount > 0)
        {
            var topSmell = config.Smells[0];
            candidates.Add((topSmell.Title, topSmell.Score));
        }

        var topLeak = leakData.CountSuspects.OrderByDescending(s => s.LeakProbability).FirstOrDefault();
        if (topLeak is not null && topLeak.LeakProbability >= 70)
            candidates.Add(($"Likely memory leak: {ShortName(topLeak.Name)} ({topLeak.LeakProbability}% probability)", topLeak.LeakProbability));

        if (exceptions.TotalAll >= 1000)
            candidates.Add(($"Exception storm: {exceptions.TotalAll:N0} exception objects on heap", 75));

        if (asyncData.BacklogTotal >= 20)
            candidates.Add(($"Async backlog: {asyncData.BacklogTotal} stuck state machines", 65));

        if (candidates.Count == 0)
            return ("No dominant issue detected — run 'analyze --full' for complete analysis", 5);

        candidates.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        return (candidates[0].Issue, candidates[0].Score);
    }

    private static List<string> BuildActionPlan(
        ConfigSmellData        config,
        ExceptionAnalysisData  exceptions,
        MemoryLeakData         leakData,
        AsyncStacksData        asyncData,
        DeadlockData           deadlocks,
        ThreadAnalysisData     threads)
    {
        var plan = new List<(string Action, int Priority)>();

        if (deadlocks.ConfirmedCycles.Count > 0)
            plan.Add(("Run 'deadlock-detection' to see the full wait-for graph and identify the lock ordering problem.", 100));

        if (exceptions.FatalFlags.HasOom)
            plan.Add(("Run 'memory-leak' and 'heap-stats' — OutOfMemoryException indicates heap exhaustion.", 98));

        if (asyncData.BacklogTotal >= 100)
            plan.Add(($"Run 'async-stacks' — {asyncData.BacklogTotal} suspended state machines suggest sync-over-async blocking.", 90));

        if (config.SmellCount > 0)
            plan.Add(($"Fix top configuration smell: {config.Smells[0].Title}. {config.Smells[0].Remediation}", 80));

        var topLeak = leakData.CountSuspects.OrderByDescending(s => s.LeakProbability).FirstOrDefault();
        if (topLeak is not null && topLeak.LeakProbability >= 70)
            plan.Add(($"Investigate leak: run 'gc-roots' for '{ShortName(topLeak.Name)}' ({topLeak.LeakProbability}% leak probability).", 70));

        plan.Sort(static (a, b) => b.Priority.CompareTo(a.Priority));
        return plan.Take(3).Select(p => p.Action).ToList();
    }

    private static string ShortName(string fullName)
    {
        int bracket = fullName.IndexOf('[');
        if (bracket > 0) fullName = fullName[..bracket];
        int dot = fullName.LastIndexOf('.');
        return dot >= 0 ? fullName[(dot + 1)..] : fullName;
    }
}
