using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Tracing;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

/// <summary>
/// Renders the cross-source correlation findings produced by <c>TraceDumpCorrelator</c>
/// — patterns that require BOTH a trace file and a dump file to detect.
/// </summary>
public sealed class TraceDumpCorrelationReport
{
    public void Render(
        IReadOnlyList<CorrelationFinding> findings,
        DumpSnapshot                      snap,
        IRenderSink                       sink)
    {
        sink.Explain(
            what: "Cross-source correlation — patterns that require BOTH a trace file and a memory dump to detect.",
            why: "A trace shows WHEN things happened (GC pauses, contention, SQL queries, exceptions). " +
                 "A dump shows WHAT the heap looked like AT that moment (live objects, async backlog, thread states). " +
                 "Rules that fire only when both signals agree carry much higher confidence than either source alone.",
            impact: "Findings here represent the strongest diagnostic signal in the combined report. " +
                    "A 'Critical' cross-source finding typically identifies the root cause of the incident.",
            bullets: [
                "Alloc type convergence — top allocator in trace is also the largest type in the dump",
                "Async starvation confirmed — trace starvation signal + dump async backlog count",
                "Exception accumulation — exception storm in trace + live exception objects in dump",
                "GC pause × LOH fragmentation — elevated pause + fragmented LOH both visible",
                "Thread contention × blocked threads — trace contention + dump blocked-thread count",
                "Slow SQL × connection count — slow queries + elevated live connection count",
                "Pinned handles × GC pause — pinning pressure visible from both sides",
                "Finalizer queue backlog — high alloc rate + deep finalizer queue",
                "HTTP latency × async backlog — slow HTTP + stuck state machines",
                "CPU saturation × thread pool idle — near-full CPU + minimal idle workers",
            ],
            action: "Work through Critical findings first. Each finding lists actionable dump commands to run next."
        );

        // ── Dump identity banner ──────────────────────────────────────────────
        sink.Section("Session Artifacts", "artifacts");
        sink.KeyValues([
            ("Dump file",     Path.GetFileName(snap.DumpPath)),
            ("Dump size",     DumpHelpers.FormatSize(snap.DumpFileSizeBytes)),
            ("CLR version",   snap.ClrVersion ?? "(unknown)"),
            ("Heap size",     DumpHelpers.FormatSize(snap.TotalHeapBytes)),
            ("Total objects", snap.TotalObjectCount.ToString("N0")),
            ("Threads",       $"{snap.AliveThreadCount} alive, {snap.BlockedThreadCount} blocked"),
            ("Health score",  $"{snap.HealthScore}/100"),
        ]);

        // ── Findings ─────────────────────────────────────────────────────────
        sink.Section("Cross-Source Findings", "cross-findings");

        if (findings.Count == 0)
        {
            sink.Alert(AlertLevel.Info,
                "No cross-source patterns detected.",
                "Either the trace and dump data do not contain corroborating signals, " +
                "or the contributing metrics are below the thresholds for each rule.",
                "This does not mean there is nothing wrong — run individual trace sub-commands " +
                "and dump commands for detailed analysis.");
            return;
        }

        int critical = 0, warning = 0, info = 0;
        foreach (var f in findings)
        {
            switch (f.Severity)
            {
                case FindingSeverity.Critical: critical++; break;
                case FindingSeverity.Warning:  warning++;  break;
                default:                       info++;     break;
            }
        }

        sink.KeyValues([
            ("Total findings", findings.Count.ToString()),
            ("Critical",       critical > 0 ? critical.ToString() : "—"),
            ("Warning",        warning > 0  ? warning.ToString()  : "—"),
            ("Info",           info > 0     ? info.ToString()     : "—"),
        ]);

        // ── Ranked findings table (replaces per-finding alert blocks) ─────────
        var rows = new List<string[]>(findings.Count);
        foreach (var f in findings)
        {
            string sev = f.Severity switch
            {
                FindingSeverity.Critical => "🔴 Critical",
                FindingSeverity.Warning  => "🟡 Warning",
                _                        => "🔵 Info",
            };
            string sources = f.ContributingAreas.Length > 0
                ? string.Join(" + ", f.ContributingAreas)
                : "—";
            rows.Add([sev, f.Score.ToString(), f.Category, f.Headline, sources]);
        }

        sink.Table(
            ["Severity", "Score", "Category", "Finding", "Sources"],
            rows,
            caption: "Findings ranked by confidence-weighted score. Expand details below for root cause explanation and remediation advice.");

        // ── Finding detail accordions ─────────────────────────────────────────
        sink.Section("Finding Details", "correlation-details");
        foreach (var f in findings)
        {
            string severity = f.Severity switch
            {
                FindingSeverity.Critical => "Critical",
                FindingSeverity.Warning  => "Warning",
                _                        => "Info",
            };
            sink.BeginDetails($"[{severity}]  {f.Category}  —  {f.Headline}  (score {f.Score}/100)", open: false);
            if (!string.IsNullOrWhiteSpace(f.Detail))
                sink.Text(f.Detail);
            if (!string.IsNullOrWhiteSpace(f.Advice))
            {
                sink.BlankLine();
                sink.Text($"Action: {f.Advice}");
            }
            sink.EndDetails();
        }

        // ── Next steps ────────────────────────────────────────────────────────
        sink.Section("Recommended Next Steps", "next-steps");

        // Derive suggested dump commands from ContributingAreas
        var dumpCmds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in findings)
        {
            if (f.Detail.Contains("memory-leak") || f.Detail.Contains("gc-roots"))
            {
                dumpCmds.Add("memory-leak");
                dumpCmds.Add("gc-roots");
            }
            if (f.ContributingAreas.Any(a => a == "dump") &&
                f.Category.Contains("Thread", StringComparison.OrdinalIgnoreCase))
            {
                dumpCmds.Add("thread-analysis");
                dumpCmds.Add("deadlock-detection");
            }
            if (f.Category.Contains("Async", StringComparison.OrdinalIgnoreCase))
                dumpCmds.Add("async-stacks");
            if (f.Category.Contains("SQL", StringComparison.OrdinalIgnoreCase))
                dumpCmds.Add("connection-pool");
            if (f.Category.Contains("GC", StringComparison.OrdinalIgnoreCase) ||
                f.Category.Contains("Memory", StringComparison.OrdinalIgnoreCase))
            {
                dumpCmds.Add("heap-stats");
                dumpCmds.Add("heap-fragmentation");
            }
            if (f.Category.Contains("Finalizer", StringComparison.OrdinalIgnoreCase))
                dumpCmds.Add("finalizer-queue");
            if (f.Category.Contains("Pinning", StringComparison.OrdinalIgnoreCase))
            {
                dumpCmds.Add("pinned-objects");
                dumpCmds.Add("handle-table");
            }
        }

        if (dumpCmds.Count > 0)
        {
            string dumpFile = Path.GetFileName(snap.DumpPath);
            var nextRows = new List<string[]>(dumpCmds.Count);
            foreach (var cmd in dumpCmds.Order())
                nextRows.Add([cmd, $"DumpDetective {cmd} {dumpFile}"]);

            sink.Table(
                ["Command", "Full invocation"],
                nextRows,
                caption: "Suggested follow-up commands derived from above findings. Run these against the captured dump.");
        }
    }
}
