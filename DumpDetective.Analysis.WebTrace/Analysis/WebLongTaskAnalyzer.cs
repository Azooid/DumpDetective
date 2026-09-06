using DumpDetective.Analysis.WebTrace.Model;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Analysis.WebTrace.Analysis;

/// <summary>
/// Ranks main-thread <c>RunTask</c> spans over the reporting floor (default 50ms — the
/// Long Tasks API threshold) by duration, then rolls them up by attributed cause —
/// "here's the one thing to fix" instead of a flat, repetitive list of durations.
/// The parser already drops everything under the floor during the streaming pass, so
/// this is a pure re-presentation, not a second filter.
/// </summary>
public static class WebLongTaskAnalyzer
{
    private const long VeryLongTaskUs = 500_000;   // 500ms — a single task this long is a user-visible freeze
    private const int  FrequentTaskCountWarn = 30;  // this many 50ms+ tasks in one recording reads as consistently janky
    private const double DominantCausePctWarn = 0.4; // one cause responsible for 40%+ of all blocking time

    public static WebLongTaskData Analyze(WebTraceData data, string traceFileName)
    {
        var tasks = data.LongTasks
            .OrderByDescending(t => t.DurationUs)
            .Select(t => new WebLongTaskRow
            {
                TimestampUs            = t.TimestampUs,
                DurationUs             = t.DurationUs,
                Pid                    = t.Pid,
                Tid                    = t.Tid,
                AttributedFunction     = t.AttributedFunction,
                AttributedUrl          = t.AttributedUrl,
                AttributedLine         = t.AttributedLine,
                AttributedResolvedFile = t.AttributedResolvedFile,
                AttributedResolvedLine = t.AttributedResolvedLine,
                AttributedSelfTimeUs   = t.AttributedSelfTimeUs,
                AttributedCallChain    = t.AttributedCallChain,
                PossibleTriggerFunction     = t.PossibleTriggerFunction,
                PossibleTriggerUrl          = t.PossibleTriggerUrl,
                PossibleTriggerLine         = t.PossibleTriggerLine,
                PossibleTriggerResolvedFile = t.PossibleTriggerResolvedFile,
                PossibleTriggerResolvedLine = t.PossibleTriggerResolvedLine,
                PossibleTriggerGapUs        = t.PossibleTriggerGapUs,
                PossibleTriggerCluster      = t.PossibleTriggerCluster,
            })
            .ToList();

        long totalUs   = data.LongTasks.Sum(t => t.DurationUs);
        long longestUs = data.LongTasks.Count > 0 ? data.LongTasks.Max(t => t.DurationUs) : 0;
        var longest    = tasks.FirstOrDefault(); // list is already ordered by duration descending

        // Roll up by attributed cause, ranked by TOTAL BLOCKED TIME (not task count) —
        // three 2-second freezes from one function matter more than ten 60ms blips from
        // another. This is the "fix this one thing" view a developer actually wants first.
        var rootCauses = tasks
            .Where(t => t.AttributedFunction is not null)
            .GroupBy(t => (t.AttributedFunction, t.AttributedLocation))
            .Select(g =>
            {
                // Most common possible trigger among this group's tasks — a timing
                // correlation across tasks, not a call-tree fact, so only surface it when
                // it's genuinely common across the group rather than a single coincidence.
                var triggerGroups = g.Where(t => t.PossibleTriggerFunction is not null)
                    .GroupBy(t => (t.PossibleTriggerFunction, t.PossibleTriggerLocation))
                    .OrderByDescending(tg => tg.Count())
                    .ToList();
                var topTrigger = triggerGroups.FirstOrDefault();
                bool triggerIsCommon = topTrigger is not null && topTrigger.Count() >= Math.Max(2, g.Count() / 2);

                // Union of every distinct trigger-cluster entry seen across this group's
                // tasks — broader than "the one most-common trigger", since a burst of
                // several application functions firing together (e.g. a grid rebuilding
                // its columns) is common and more informative shown as a whole.
                string? clusterUnion = null;
                var clusterEntries = g.Where(t => t.PossibleTriggerCluster is not null)
                    .SelectMany(t => t.PossibleTriggerCluster!.Split(", "))
                    .Distinct(StringComparer.Ordinal)
                    .Take(8)
                    .ToList();
                if (clusterEntries.Count > 0) clusterUnion = string.Join(", ", clusterEntries);

                return new WebLongTaskRootCause
                {
                    Function           = g.Key.AttributedFunction!,
                    Location           = g.Key.AttributedLocation,
                    Url                = g.First().AttributedUrl ?? "",
                    TaskCount          = g.Count(),
                    TotalDurationUs    = g.Sum(t => t.DurationUs),
                    LongestTaskUs      = g.Max(t => t.DurationUs),
                    PctOfTotalBlocking = totalUs > 0 ? g.Sum(t => t.DurationUs) * 100.0 / totalUs : 0,
                    CallChain          = g.First().AttributedCallChain,
                    PossibleTriggerFunction = triggerIsCommon ? topTrigger!.Key.PossibleTriggerFunction : null,
                    PossibleTriggerLocation = triggerIsCommon ? topTrigger!.Key.PossibleTriggerLocation : null,
                    PossibleTriggerCluster  = clusterUnion,
                };
            })
            .OrderByDescending(rc => rc.TotalDurationUs)
            .ToList();

        var findings = new List<Finding>();
        if (longestUs >= VeryLongTaskUs)
        {
            findings.Add(new Finding(FindingSeverity.Critical, "Web Performance",
                $"Longest single main-thread task blocked for {longestUs / 1000.0:F0} ms",
                Detail: longest?.AttributedFunction is not null
                    ? $"Most likely cause: '{longest.AttributedFunction}' ({longest.AttributedLocation})"
                    : null,
                Advice: "A task this long freezes the page completely — no input, scroll, or paint can happen until " +
                        "it finishes. Break it into smaller chunks (yield with setTimeout/scheduler.yield, or move it " +
                        "to a Web Worker if it doesn't need DOM access).",
                Deduction: 30));
        }

        if (tasks.Count >= FrequentTaskCountWarn)
        {
            findings.Add(new Finding(FindingSeverity.Warning, "Web Performance",
                $"{tasks.Count:N0} tasks over 50ms in this recording (total {totalUs / 1000.0:F0} ms blocking time)",
                Advice: "Frequent long tasks — even individually survivable — add up to a page that feels " +
                        "consistently janky rather than momentarily frozen. See Root Causes below for what's common " +
                        "across them.",
                Deduction: 15));
        }

        // The single most actionable finding this analyzer can produce: one cause
        // responsible for a large share of ALL blocked time, with remediation tailored
        // to whether it's actual JS (fixable by the app) or native browser work (layout/
        // GC/idle) that needs a different kind of fix.
        var topCause = rootCauses.FirstOrDefault();
        if (topCause is not null && topCause.PctOfTotalBlocking >= DominantCausePctWarn * 100)
        {
            bool isNative = topCause.Location is "(native)" or "—";
            var detailLines = new List<string> { topCause.Location };
            if (topCause.CallChain is not null) detailLines.Add($"Called from: {topCause.CallChain}");
            if (topCause.PossibleTriggerFunction is not null)
                detailLines.Add($"Possible trigger: '{topCause.PossibleTriggerFunction}' ({topCause.PossibleTriggerLocation}) — " +
                                 "your own code, seen running shortly before most of these tasks");
            if (topCause.PossibleTriggerCluster is not null)
                detailLines.Add($"Your code seen running nearby: {topCause.PossibleTriggerCluster}");

            findings.Add(new Finding(FindingSeverity.Critical, "Web Performance",
                $"'{topCause.Function}' is responsible for {topCause.PctOfTotalBlocking:F0}% of all main-thread " +
                $"blocking time ({topCause.TotalDurationUs / 1000.0:F0} ms across {topCause.TaskCount} task(s))",
                Detail: string.Join("\n", detailLines),
                Advice: topCause.PossibleTriggerFunction is not null
                    ? $"Start at '{topCause.PossibleTriggerFunction}' ({topCause.PossibleTriggerLocation}) — it's your " +
                      "own code and it runs right before this cost, most likely scheduling it via setTimeout/a promise " +
                      "(that's why it isn't in the call chain above — V8's profiler can't link across that boundary). " +
                      "This is a timing correlation, not a certainty — confirm it before assuming it's the cause."
                    : topCause.PossibleTriggerCluster is not null
                    ? $"No single function was consistently the closest, but your own code ran repeatedly right " +
                      "before these tasks — see 'Your code seen running nearby' above and check web-long-tasks' " +
                      "per-task table for the full per-task breakdown."
                    : isNative
                        ? "This is native browser work (layout/GC/idle), not application JS — check web-cpu-hotspots for " +
                          "a forced-layout ('Web Rendering') finding and web-gc-pressure for GC cycles, rather than " +
                          "looking for a JS function to optimize directly."
                        : "Fixing this one function first removes more blocking time than any other single change would. " +
                          "Check its call count in web-cpu-hotspots — a cheap function called far too often costs as " +
                          "much as an expensive one called rarely, and the fix is different (reduce calls vs. optimize the body).",
                Deduction: 25));
        }

        if (findings.Count == 0)
            findings.Add(new Finding(FindingSeverity.Info, "Summary",
                data.LongTasks.Count == 0
                    ? "No tasks over 50ms found — main thread stayed responsive throughout the recording."
                    : "A handful of long tasks were found but not enough to flag on their own."));

        return new WebLongTaskData
        {
            TraceInfo           = traceFileName,
            Tasks               = tasks,
            RootCauses          = rootCauses,
            TotalBlockingTimeUs = totalUs,
            LongestTaskUs       = longestUs,
            Findings            = findings,
        };
    }
}
