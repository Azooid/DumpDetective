using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Tracing;

namespace DumpDetective.Reporting.Reports;

public sealed class CpuTraceReport
{
    public void Render(CpuTraceData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "CPU sampling trace analysis — call tree, hot path, and per-method inclusive/exclusive CPU time.",
            why: "CPU samples capture the executing stack at regular intervals. Exclusive time = the method actually running; inclusive time = the method or anything it called was running.",
            impact: "Methods with high exclusive % are direct CPU consumers. The hot path is the single chain of highest-inclusive callers from the root to the deepest hot node — identical to Visual Studio's 'Hot Path' highlight.",
            bullets: [
                "Exclusive % → method is itself the bottleneck (tight loops, heavy computation)",
                "Inclusive % → method or its callees are expensive (coordination overhead, deep call chains)",
                "Hot path → the most critical execution chain in the trace"
            ],
            action: "Focus optimisation on methods with high exclusive % first. If a method has high inclusive % but low exclusive %, investigate its callees in the call tree."
        );

        sink.Section("Trace Summary", "cpu-summary");
        sink.KeyValues([
            ("Trace",             data.TraceInfo),
            ("Total CPU samples", data.TotalSamples.ToString("N0")),
            ("Process filter",    data.FilteredProcess ?? "(all processes)"),
        ]);

        if (data.Stats is { } s)
        {
            string duration = s.TraceDurationMs >= 60_000
                ? $"{s.TraceDurationMs / 60_000:F1} min"
                : $"{s.TraceDurationMs / 1000:F1} s";

            sink.KeyValues([
                ("Duration",       duration),
                ("Total CPU time", $"{s.TotalCpuMs / 1000:F1} s"),
                ("Active threads", s.ActiveThreads.ToString("N0")),
                ("Logical cores",  s.LogicalCores.ToString()),
                ("Top process",    s.TopProcessName),
            ]);

            sink.Gauges([
                ("Avg CPU",        s.AvgCpuPct,  "%"),
                ("Peak CPU (1 s)", s.MaxCpuPct,  "%"),
            ], barMax: 100.0);

            if (s.MaxCpuPct >= 90)
                sink.Alert(AlertLevel.Critical,
                    $"CPU saturation: peak {s.MaxCpuPct:F1}% — one or more cores were at 100% during the capture.",
                    "The system was CPU-bound. High-exclusive methods in the top table are the primary candidates.");
            else if (s.AvgCpuPct >= 70)
                sink.Alert(AlertLevel.Warning,
                    $"High average CPU: {s.AvgCpuPct:F1}% sustained across the trace duration.",
                    "Sustained high CPU often indicates an algorithmic bottleneck rather than a single hot method.");
        }

        // CPU utilisation over time — sparkline (one point per second, in CPU %)
        if (data.SamplesTimeline is { Count: > 2 } timeline)
            sink.Sparkline(timeline, "CPU utilisation over time (per-second)", "%");

        if (data.TotalSamples == 0)
        {
            sink.Alert(AlertLevel.Warning, "No CPU samples found in trace.",
                "To capture CPU samples, re-collect with one of the following:",
                "dotnet-trace:\n" +
                "  dotnet-trace collect --profile cpu-sampling\n\n" +
                "PerfView:\n" +
                "  PerfView.exe /ClrEvents:Stack,Default /NoGui collect");
            return;
        }

        // Warn when a large fraction of samples are unresolved.
        // Case A — ManagedModule: CLR rundown events absent, managed methods unattributed.
        // Case B — [unresolved]:  no module info at all (PerfView <<<?!?>>>); bulk of samples in
        //          traces collected without proper ETW providers. This is the dominant case when
        //          the call tree looks empty despite a high sample count.
        if (data.UnresolvedSamples > 0 && data.TotalSamples > 0)
        {
            double unresolvedPct = data.UnresolvedSamples * 100.0 / data.TotalSamples;
            if (unresolvedPct >= 5.0)
            {
                sink.Alert(AlertLevel.Warning,
                    $"Symbol resolution incomplete: {unresolvedPct:F1}% of samples " +
                    $"({data.UnresolvedSamples:N0} / {data.TotalSamples:N0}) could not be attributed " +
                    "to a named method. These appear in the call tree as '\u003cunresolved\u003e' or " +
                    "'\u003cmanaged, no symbols\u003e' and dominate the exclusive-CPU column.",
                    "The trace is missing CLR rundown events and/or kernel symbol data. " +
                    "Re-collect with one of the commands below to get full attribution:",
                    "dotnet-trace (managed + rundown):\n" +
                    "  dotnet-trace collect --profile cpu-sampling --clrevents default+rundown\n\n" +
                    "PerfView (full symbol resolution):\n" +
                    "  PerfView.exe /ClrEvents:Stack,Default,Rundown /NoGui collect\n\n" +
                    "xperf / WPR:\n" +
                    "  wpr -start CPU -start DotNet\n" +
                    "  wpr -stop trace.etl");
            }
        }

        RenderHotPath(sink, data);
        RenderTopMethods(sink, data, top);
        RenderCallTree(sink, data, top);
        RenderSemanticFindings(sink, data);
        RenderHotChains(sink, data);
        RenderCategoryScores(sink, data);
    }

    // ── Hot Path ─────────────────────────────────────────────────────────────

    private static void RenderHotPath(IRenderSink sink, CpuTraceData data)
    {
        if (data.HotPath.Count == 0) return;

        sink.Section("Hot Path", "cpu-hot-path");
        sink.Alert(AlertLevel.Info,
            "Hot path = the deepest chain of maximum CPU consumption, following highest-inclusive callers at each level.",
            detail: "This is the same chain Visual Studio highlights in orange in the CPU Usage view.");

        string caption = $"Hot path depth: {data.HotPath.Count} frames  |  deepest → {TrimMethod(data.HotPath[^1].Method, 60)}";
        sink.CallTree(data.HotPath, caption, topN: data.HotPath.Count);
    }

    // ── Top Methods ──────────────────────────────────────────────────────────

    private static void RenderTopMethods(IRenderSink sink, CpuTraceData data, int top)
    {
        sink.Section("Top Methods by CPU (Exclusive)", "cpu-top-methods");

        if (data.TopMethods.Count == 0)
        {
            sink.Text("No methods with exclusive samples found.");
            return;
        }

        var rows = data.TopMethods.Take(top)
            .Select(m => new[]
            {
                TrimMethod(m.Method, 80),
                m.Module,
                $"{m.ExclusivePct:F1}%",
                $"{m.InclusivePct:F1}%",
                m.ExclusiveSamples.ToString("N0"),
                m.InclusiveSamples.ToString("N0"),
            })
            .ToList();

        double exclusiveTotal = data.TopMethods.Take(top).Sum(m => m.ExclusivePct);

        // Exclusive CPU % broken down by module — above the table for quick visual
        var byModule = data.TopMethods
            .GroupBy(m => m.Module.Length > 0 ? m.Module : "(unknown)")
            .Select(g => (Label: g.Key, Value: g.Sum(m => m.ExclusivePct)))
            .OrderByDescending(t => t.Value)
            .Take(8)
            .ToList();
        if (byModule.Count > 0)
            sink.StackedBar(byModule, "%", "Exclusive CPU % by module (from top methods)");

        sink.Table(
            ["Method", "Module", "Excl %", "Incl %", "Excl samples", "Incl samples"],
            rows,
            $"Top {rows.Count} methods account for {exclusiveTotal:F1}% of CPU  |  " +
            $"{data.TotalSamples:N0} total samples");
    }

    // ── Call Tree ────────────────────────────────────────────────────────────

    private static void RenderCallTree(IRenderSink sink, CpuTraceData data, int top)
    {
        if (data.CallTree.Count == 0) return;

        sink.Section("Call Tree (top roots)", "cpu-call-tree");
        sink.CallTree(data.CallTree,
            $"Showing top {Math.Min(top, data.CallTree.Count)} call roots sorted by inclusive CPU.",
            topN: top);
    }

    // ── Semantic Findings ────────────────────────────────────────────────────

    private static void RenderSemanticFindings(IRenderSink sink, CpuTraceData data)
    {
        if (data.SemanticFindings is not { Count: > 0 } findings) return;

        sink.Section("Semantic Findings", "cpu-semantic-findings");
        sink.Alert(AlertLevel.Info,
            "Semantic findings identify architectural patterns and framework-specific bottlenecks — not just hot methods.",
            detail: "Each finding names the pattern, explains the root cause, and provides actionable advice.");

        var rows = new List<string[]>(findings.Count);
        for (int i = 0; i < findings.Count; i++)
        {
            var f = findings[i];
            string severity = f.Severity switch
            {
                Core.Models.FindingSeverity.Critical => "Critical",
                Core.Models.FindingSeverity.Warning  => "Warning",
                _                                    => "Info"
            };
            rows.Add([severity, f.Category, f.Headline, $"{f.Score}/100",
                      f.Advice ?? ""]);
        }

        sink.Table(
            ["Severity", "Category", "Finding", "Score", "Advice"],
            rows,
            $"{findings.Count} semantic pattern(s) detected");

        // Emit an alert for any Critical/Warning findings
        for (int i = 0; i < findings.Count; i++)
        {
            var f = findings[i];
            if (f.Severity == Core.Models.FindingSeverity.Info) continue;

            var level = f.Severity == Core.Models.FindingSeverity.Critical
                ? AlertLevel.Critical
                : AlertLevel.Warning;

            sink.Alert(level, f.Headline, f.Detail, f.Advice);
        }
    }

    // ── Hot Chains ────────────────────────────────────────────────────────────

    private static void RenderHotChains(IRenderSink sink, CpuTraceData data)
    {
        if (data.HotChains is not { Count: > 0 } chains) return;

        sink.Section("Hot Chains", "cpu-hot-chains");
        sink.Alert(AlertLevel.Info,
            "A hot chain traces from a significant entry-point (high inclusive%) down to the leaf burning the most exclusive CPU.",
            detail: "This is your root-cause chain: the entry point that owns the work, and the method actually executing it.");

        var rows = new List<string[]>(chains.Count);
        for (int i = 0; i < chains.Count; i++)
        {
            var c = chains[i];
            // Full chain — all frames, IL noise cleaned, no truncation
            var parts = new string[c.Chain.Count];
            for (int j = 0; j < c.Chain.Count; j++) parts[j] = CleanIlMethod(c.Chain[j]);
            string chainStr = string.Join(" → ", parts);
            rows.Add([
                CleanIlMethod(c.RootMethod),
                $"{c.OwnerPct:F1}%",
                CleanIlMethod(c.ExclusiveHotMethod),
                $"{c.HotPct:F1}%",
                chainStr,
            ]);
        }

        sink.Table(
            ["Entry Point", "Owns (Incl%)", "Hot Leaf", "Hot Leaf (Excl%)", "Chain"],
            rows,
            $"{chains.Count} hot chain(s) — entry point → hot executing method");
    }

    // ── Category Scores ───────────────────────────────────────────────────────

    private static void RenderCategoryScores(IRenderSink sink, CpuTraceData data)
    {
        if (data.CategoryScores is not { Count: > 0 } scores) return;

        sink.Section("Diagnostic Category Scores", "cpu-category-scores");
        sink.Alert(AlertLevel.Info,
            "Category scores (0–100) summarise the severity of each detected pattern. Higher = more urgent.",
            detail: "Scores are derived from inclusive CPU% combined with pattern confidence. Focus on categories scoring ≥ 75 first.");

        var rows = new List<string[]>(scores.Count);
        for (int i = 0; i < scores.Count; i++)
        {
            var s = scores[i];
            string urgency = s.Score switch { >= 85 => "High", >= 65 => "Medium", _ => "Low" };
            rows.Add([s.Category, $"{s.Score}/100", urgency,
                      s.FindingCount.ToString(), TrimMethod(s.TopFinding.Headline, 80)]);
        }

        sink.Table(
            ["Category", "Score", "Urgency", "Findings", "Top Finding"],
            rows,
            "Categories ranked by score");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string CleanIlMethod(string method) =>
        TraceReportHelpers.CleanIlMethod(method);

    private static string TrimMethod(string method, int maxLen)
    {
        method = CleanIlMethod(method);
        if (method.Length <= maxLen) return method;

        // Strategy: keep ClassName.MethodName(…) — drop namespace prefix and param details.
        // 1. Find the parameter list start
        int parenIdx = method.IndexOf('(');
        string namePart   = parenIdx > 0 ? method[..parenIdx] : method;
        string paramPart  = parenIdx > 0 ? method[parenIdx..] : "";

        // 2. Find last two dots in the name part → "Class.Method"
        int lastDot       = namePart.LastIndexOf('.');
        int secondLastDot = lastDot > 0 ? namePart.LastIndexOf('.', lastDot - 1) : -1;
        int nameStart     = secondLastDot >= 0 ? secondLastDot + 1 : (lastDot >= 0 ? lastDot + 1 : 0);
        string shortName  = namePart[nameStart..];

        // 3. Try: "Class.Method(params)" — truncate params if needed
        if (paramPart.Length > 0)
        {
            string candidate = shortName + paramPart;
            if (candidate.Length <= maxLen) return candidate;

            // Truncate params: "Class.Method(…)"
            string withEllipsis = shortName + "(…)";
            if (withEllipsis.Length <= maxLen) return withEllipsis;
        }

        // 4. Just the short name, truncated if still too long
        return shortName.Length <= maxLen ? shortName : shortName[..(maxLen - 1)] + "…";
    }
}
