using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class ProcessLifecycleReport
{
    public void Render(ProcessLifecycleData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "Process lifecycle analysis from Process/Start and Process/Stop events — detects restarts, abnormal exits, and process churn during the trace window.",
            why: "Unexpected process restarts indicate crashes, watchdog intervention, or orchestrator-level remediation, all of which cause request failures and state loss.",
            impact: "Each restart of a .NET process discards all in-memory state, JIT cache, and connection pools, causing a cold-start latency spike for subsequent requests.",
            bullets: [
                "Detected restarts   — processes that stopped and restarted within 30 seconds",
                "Abnormal stops      — processes with non-zero exit codes",
                "Process groups      — summary by process name"
            ],
            action: "Investigate non-zero exit codes with Windows Event Viewer or container logs. " +
                    "Use structured exception handling to log unhandled exceptions before exit. " +
                    "Ensure graceful shutdown handles SIGTERM/CancelKeyPress."
        );

        sink.Section("Trace Summary", "proclife-summary");
        sink.KeyValues([
            ("Trace",             data.TraceInfo),
            ("Total starts",      data.TotalStarts.ToString("N0")),
            ("Total stops",       data.TotalStops.ToString("N0")),
            ("Detected restarts", data.DetectedRestarts > 0
                                   ? $"{data.DetectedRestarts} ⚠" : "0"),
            ("Abnormal stops",    data.AbnormalStops > 0
                                   ? $"{data.AbnormalStops} ⚠" : "0"),
            ("Process filter",    data.FilteredProcess ?? "(all processes)"),
        ]);

        if (!data.HasData)
        {
            sink.Alert(AlertLevel.Info, "No Process/Start or Process/Stop events found.",
                "These events are typically available in ETL traces with the Kernel Process provider.",
                "PerfView: check 'KernelProcess' option. xperf: -on PROC_THREAD");
            return;
        }

        if (data.DetectedRestarts > 0)
            sink.Alert(AlertLevel.Critical,
                $"{data.DetectedRestarts} process restart(s) detected.",
                "A process stopped and restarted within 30 seconds — indicating a crash or watchdog restart.");

        if (data.AbnormalStops > 0)
            sink.Alert(AlertLevel.Warning,
                $"{data.AbnormalStops} process(es) exited with a non-zero exit code.",
                "Non-zero exits indicate unhandled exceptions, OOM kills, or orchestrator intervention.");

        if (data.ProcessGroups.Count > 0)
        {
            sink.Section("Process Groups", "proclife-groups");
            var rows = new List<string[]>(data.ProcessGroups.Count);
            foreach (var g in data.ProcessGroups.Take(top))
                rows.Add([g.ProcessName, g.Starts.ToString("N0"), g.Stops.ToString("N0"),
                           g.Restarts.ToString("N0"), g.AbnormalStops.ToString("N0")]);
            sink.Table(
                ["Process Name", "Starts", "Stops", "Restarts", "Abnormal Exits"],
                rows, "Groups ordered by restart count descending");
        }

        if (data.Events.Count > 0)
        {
            sink.Section("Process Events (chronological)", "proclife-events");
            var rows = new List<string[]>(Math.Min(top * 2, data.Events.Count));
            foreach (var e in data.Events.Take(top * 2))
                rows.Add([$"{e.TimeMs:F0} ms", e.EventType, e.ProcessName,
                           e.ProcessId.ToString(), e.ExitCode.HasValue ? e.ExitCode.Value.ToString() : "—"]);
            sink.Table(
                ["Time Offset", "Event", "Process Name", "PID", "Exit Code"],
                rows, "Chronological process events");
        }
    }
}
