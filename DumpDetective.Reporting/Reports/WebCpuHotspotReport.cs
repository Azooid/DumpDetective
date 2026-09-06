using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class WebCpuHotspotReport
{
    public void Render(WebCpuHotspotData data, IRenderSink sink)
    {
        sink.Section("CPU Hotspots");
        sink.Explain(
            what: "Main-thread CPU self-time aggregated by (source file, function, line) from the V8 CPU profiler " +
                  "samples embedded in the trace — every source location ranked by how much CPU time it actually cost.",
            why:  "Self-time (not inclusive time) tells you where the CPU was actually spent, not just which call " +
                  "stack was active. A function with a small per-call cost but a huge call count costs just as much " +
                  "as one expensive call — check the sample/call count next to the time, not just the time alone.",
            bullets:
            [
                "One function dominating (>20-35% of all sampled CPU) → hot loop or unexpectedly high call count",
                "'get offsetWidth' / 'getBoundingClientRect' high in the list → forced synchronous layout (layout thrashing)",
                "Vendor/CDN URL at the top → the fix may not be in your code — check whether it's called excessively from yours",
                "Shared-utility function at the top (e.g. a CSS/size getter) → its own name won't say which feature is " +
                  "responsible; the Call Stack column shows the caller chain that actually reveals it",
            ],
            action: "Open the file at the reported line first — this list is ranked so the top row is the single " +
                    "highest-value place to start. Look for the ★ marker in Call Stack — it's where your own code " +
                    "enters the stack. If a chain has none, check the '~ your code seen running nearby' line before " +
                    "assuming the fix isn't in your code.");

        // The top-N ranked list by self-time, plus every first-party hotspot that would
        // otherwise be cut off by that ranking (either directly — see Application Code
        // below — or as a vendor hotspot whose chain reaches back into your code) so
        // nothing relevant is ever hidden below the fold. Rows still sort by self-time,
        // so this only *adds* rows past the top-N cutoff — it never reorders it.
        var rows = data.Top
            .Concat(data.LinkedToApplicationCode)
            .Concat(data.ApplicationCode.Take(5))
            .DistinctBy(r => (r.FunctionName, r.Location))
            .OrderByDescending(r => r.SelfTimeUs)
            .ToList();

        sink.KeyValues([
            ("Total sampled CPU time", $"{data.TotalSampledUs / 1000.0:F0} ms"),
            ("Unique call-frames",     rows.Count.ToString("N0")),
        ]);

        sink.Table(
            ["Self Time", "% of CPU", "Samples", "Function", "Call Stack"],
            rows.Select(r =>
            {
                var chain = WebCallChainHelper.BuildChain(r.FunctionName, r.Location, r.Url, r.SampleCount, r.PctOfTotal * 100, r.CallChain);
                var stack = WebCallChainHelper.FormatStackText(chain);
                if (r.PossibleTriggerCluster is not null)
                    stack = WebCallChainHelper.AppendPossibleTrigger(stack, r.PossibleTriggerCluster);
                return new[]
                {
                    $"{r.SelfTimeUs / 1000.0:F1} ms",
                    $"{r.PctOfTotal * 100:F1}%",
                    r.SampleCount.ToString("N0"),
                    r.FunctionName.Length > 50 ? r.FunctionName[..50] + "…" : r.FunctionName,
                    stack,
                };
            }).ToList(),
            "Ranked by self-time descending. Call Stack reads like a .NET stack trace — the hotspot itself is the " +
            "first line (its source location, de-minified where available), each line below it is the next caller " +
            "up; ★ marks where your own code enters the stack.");

        RenderApplicationCode(data, sink);

        sink.Section("Findings");
        foreach (var f in data.Findings)
            sink.Alert(
                f.Severity switch
                {
                    FindingSeverity.Critical => AlertLevel.Critical,
                    FindingSeverity.Warning  => AlertLevel.Warning,
                    _                        => AlertLevel.Info,
                },
                f.Headline, f.Detail, f.Advice);

        sink.Section("Next Steps");
        sink.KeyValues([
            ("Memory / listener leak trend for this trace", "web-memory-leak <trace.json.gz>"),
        ]);
    }

    private static void RenderApplicationCode(WebCpuHotspotData data, IRenderSink sink)
    {
        if (data.ApplicationCode.Count == 0) return;

        sink.Section("Application Code");
        sink.Explain(
            what: "Every first-party function (your source, not a vendor/library path) found anywhere in the CPU " +
                  "profile — shown regardless of self-time rank, unlike the ranked list above.",
            why:  "A thin function that only *schedules* expensive work — e.g. calling setTimeout and returning — " +
                  "can have almost no self-time of its own and never appear in a top-N self-time ranking, even " +
                  "though it's the actual trigger a developer needs to see. This section exists so that case is " +
                  "never invisible.",
            bullets:
            [
                "Low self-time here doesn't mean 'not the cause' — it may schedule the real work asynchronously",
                "V8's CPU profiler (and DevTools itself) cannot link a setTimeout/promise callback back to whoever " +
                  "scheduled it — only a timestamp coincidence with web-long-tasks can suggest that connection, " +
                  "never a call-tree link",
            ]);

        sink.Table(
            ["Self Time", "Samples", "Function", "Location"],
            data.ApplicationCode.Select(r => new[]
            {
                $"{r.SelfTimeUs / 1000.0:F1} ms",
                r.SampleCount.ToString("N0"),
                r.FunctionName.Length > 50 ? r.FunctionName[..50] + "…" : r.FunctionName,
                r.Location.Length > 80 ? "…" + r.Location[^79..] : r.Location,
            }).ToList(),
            $"{data.ApplicationCode.Count} first-party function(s) found in this recording, ranked by self-time.");
    }
}
