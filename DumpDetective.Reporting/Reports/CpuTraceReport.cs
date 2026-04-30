using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

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

        if (data.TotalSamples == 0)
        {
            sink.Alert(AlertLevel.Warning, "No CPU samples found in trace.",
                "Ensure the trace was collected with CPU sampling enabled.",
                "dotnet-trace: use --profile cpu-sampling\nPerfView: check 'Cpu Samples' in the collection dialog");
            return;
        }

        RenderHotPath(sink, data);
        RenderTopMethods(sink, data, top);
        RenderCallTree(sink, data, top);
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

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string TrimMethod(string method, int maxLen)
    {
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
