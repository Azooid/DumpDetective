using DumpDetective.Analysis.WebTrace.Model;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.WebTrace.Analysis;

/// <summary>
/// Ranks aggregated CPU self-time by (url, function, line) — the "which file do I look at"
/// answer for a slow/janky recording. Pure POCO over the parser's already-reduced hotspot
/// table; never touches the raw trace.
/// </summary>
public static class WebCpuHotspotAnalyzer
{
    // A single call-frame responsible for this much of all sampled CPU time is unusual
    // enough on its own to call out, regardless of its absolute duration.
    private const double DominantPctCritical = 0.35;
    private const double DominantPctWarn     = 0.20;

    private const int MaxLinkedToApplicationCode = 10;

    public static WebCpuHotspotData Analyze(WebTraceData data, string traceFileName, int top = 25)
    {
        long total = data.CpuHotspots.Sum(h => h.SelfTimeUs);

        // Union of trigger-cluster entries seen across every long task this hotspot was the
        // attributed cause of, keyed the same way the parser keys hotspots (Url|Function|Line)
        // — the same rollup web-long-tasks' Root Causes does, joined here by hotspot identity
        // so a leaf hotspot with no synchronous application caller (e.g. reached only via a
        // promise/deferred callback the CPU profiler can't trace through) still shows the one
        // signal that *can* survive that boundary: first-party code seen running just before it.
        var triggerClusterByKey = data.LongTasks
            .Where(t => t.AttributedFunction is not null && t.PossibleTriggerCluster is not null)
            .GroupBy(t => $"{t.AttributedUrl}|{t.AttributedFunction}|{t.AttributedLine}", StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => string.Join(", ", g.SelectMany(t => t.PossibleTriggerCluster!.Split(", "))
                                        .Distinct(StringComparer.Ordinal)
                                        .Take(8)),
                StringComparer.Ordinal);

        WebCpuHotspotRow ToRow(WebCpuHotspot h)
        {
            var appCaller = FindApplicationCaller(h.CallChain);
            string? trigger = appCaller is null &&
                triggerClusterByKey.TryGetValue($"{h.Url}|{h.FunctionName}|{h.Line}", out var cluster)
                    ? cluster : null;
            return new WebCpuHotspotRow
            {
                Url          = h.Url,
                FunctionName = h.FunctionName,
                Line         = h.Line,
                SelfTimeUs   = h.SelfTimeUs,
                SampleCount  = h.SampleCount,
                PctOfTotal   = total > 0 ? (double)h.SelfTimeUs / total : 0,
                ResolvedFile = h.ResolvedFile,
                ResolvedLine = h.ResolvedLine,
                CallChain    = h.CallChain,
                AppCallerFunction = appCaller?.Function,
                AppCallerLocation = appCaller?.Location,
                PossibleTriggerCluster = trigger,
            };
        }

        var rows = data.CpuHotspots.Take(top).Select(ToRow).ToList();

        // Every first-party function found anywhere in the profile, regardless of self-time
        // rank — a thin application function that only *schedules* expensive work (e.g. via
        // setTimeout) can have negligible self-time of its own and never make the top-N cut,
        // even though it's the actual trigger a developer needs to see. Ranked by self-time
        // among just this subset so the most-sampled first-party code still comes first.
        var allRows = data.CpuHotspots.Select(ToRow).ToList();
        var applicationCode = allRows
            .Where(r => r.IsApplicationCode)
            .OrderByDescending(r => r.SelfTimeUs)
            .ToList();

        // The explicit bridge between "Called From" and "Application Code": the top
        // hotspots by self-time are often pure vendor-to-vendor call chains with no
        // application caller within reach at all (confirmed against a real trace — the
        // top 5 hotspots there had zero), while plenty of *lower-ranked* hotspots do trace
        // back into application code. Surfacing those explicitly, ranked by their own
        // self-time, answers "where's the connection" instead of leaving the reader to
        // notice its absence in the top-N list.
        var linkedToApplicationCode = allRows
            .Where(r => !r.IsApplicationCode && r.AppCallerFunction is not null)
            .OrderByDescending(r => r.SelfTimeUs)
            .Take(MaxLinkedToApplicationCode)
            .ToList();

        var findings = new List<Finding>();
        var top1 = rows.FirstOrDefault();
        if (top1 is not null && top1.PctOfTotal >= DominantPctCritical)
        {
            findings.Add(new Finding(FindingSeverity.Critical, "Web Performance",
                $"'{top1.FunctionName}' responsible for {top1.PctOfTotal * 100:F0}% of all sampled main-thread CPU " +
                $"({top1.SelfTimeUs / 1000.0:F0} ms across {top1.SampleCount:N0} samples)",
                Detail: LocationOf(top1),
                Advice: "One function dominating CPU this heavily is almost always a hot loop or a function called " +
                        "far more often than intended (e.g. once per row/render instead of once). Check the call " +
                        "count, not just the duration — a cheap function called tens of thousands of times costs as " +
                        "much as an expensive one called a handful of times.",
                Deduction: 40));
        }
        else if (top1 is not null && top1.PctOfTotal >= DominantPctWarn)
        {
            findings.Add(new Finding(FindingSeverity.Warning, "Web Performance",
                $"'{top1.FunctionName}' responsible for {top1.PctOfTotal * 100:F0}% of all sampled main-thread CPU",
                Detail: LocationOf(top1),
                Deduction: 15));
        }

        // Forced synchronous layout: reading layout properties (offsetWidth, getBoundingClientRect,
        // etc.) shows up as native call frames with no url/line — "get offsetWidth" et al.
        var layoutRow = rows.FirstOrDefault(r =>
            r.Url.Length == 0 &&
            (r.FunctionName.StartsWith("get offset", StringComparison.Ordinal) ||
             r.FunctionName.StartsWith("get client",  StringComparison.Ordinal) ||
             r.FunctionName is "getBoundingClientRect" or "getComputedStyle"));
        if (layoutRow is not null && layoutRow.PctOfTotal >= DominantPctWarn)
        {
            findings.Add(new Finding(FindingSeverity.Warning, "Web Rendering",
                $"'{layoutRow.FunctionName}' cost {layoutRow.SelfTimeUs / 1000.0:F0} ms " +
                $"({layoutRow.PctOfTotal * 100:F0}% of sampled CPU) — forced synchronous layout",
                Advice: "Reading a layout property forces the browser to recompute layout immediately instead of " +
                        "batching it with the next frame. Called in a loop (e.g. measuring N rows one at a time), " +
                        "this is the classic 'layout thrashing' pattern. Batch all reads before any writes.",
                Deduction: 20));
        }

        // A first-party function present in the profile but absent from the top-N ranking
        // is easy to miss entirely — call it out explicitly rather than leave it silent,
        // since this is exactly the shape of a thin trigger function (e.g. one that just
        // schedules real work via setTimeout) that self-time ranking structurally can't surface.
        if (applicationCode.Count > 0 && !rows.Any(r => r.IsApplicationCode))
        {
            var topApp = applicationCode[0];
            findings.Add(new Finding(FindingSeverity.Info, "Web Performance",
                $"Your own code ('{topApp.FunctionName}' in {topApp.Location}) appears in this profile but not in " +
                $"the top {top} by self-time — see Application Code below.",
                Advice: "A low self-time doesn't rule this out as the trigger: if it schedules work via setTimeout " +
                        "or a promise, the actual expensive code runs in a separate task with no call-tree link back " +
                        "to it — V8's CPU profiler (and DevTools itself) can't show that connection, only the timing " +
                        "coincidence. If you suspect a specific interaction, correlate its timestamp against " +
                        "web-long-tasks manually.",
                Deduction: 0));
        }

        if (findings.Count == 0)
            findings.Add(new Finding(FindingSeverity.Info, "Summary",
                "No single call-frame dominates main-thread CPU in this recording."));

        return new WebCpuHotspotData
        {
            TraceInfo               = traceFileName,
            TotalSampledUs          = total,
            Top                     = rows,
            ApplicationCode         = applicationCode,
            LinkedToApplicationCode = linkedToApplicationCode,
            Findings                = findings,
        };
    }

    private static string LocationOf(WebCpuHotspotRow r) =>
        r.Url.Length > 0 ? r.Location : "(native / no source location)";

    private const char CallChainDelim = ''; // matches ChromeTraceParser.BuildCallChain's entry encoding

    /// <summary>Mirrors <c>WebCallChainHelper.FindApplicationCaller</c> (Reporting) — duplicated here since the two live in different assemblies, same as the small <c>IsApplicationCode</c> heuristic already is.</summary>
    private static (string Function, string Location)? FindApplicationCaller(string? callChain)
    {
        if (callChain is null) return null;
        foreach (var entry in callChain.Split(" ← "))
        {
            var parts = entry.Split(CallChainDelim);
            if (parts.Length > 2 && IsApplicationUrl(parts[2]))
                return (parts[0], parts.Length > 1 ? parts[1] : "");
        }
        return null;
    }

    private static bool IsApplicationUrl(string url) =>
        url.Length > 0 &&
        !url.Contains("node_modules", StringComparison.OrdinalIgnoreCase) &&
        !url.Contains("/.vite/deps/", StringComparison.OrdinalIgnoreCase) &&
        !url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase);
}
