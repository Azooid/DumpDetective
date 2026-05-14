using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class OpenTelemetryTraceReport
{
    public void Render(OpenTelemetryTraceData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "OpenTelemetry / DiagnosticSource Activity analysis — parses ActivityStart/ActivityStop events to measure operation latency, error rates, and slow spans.",
            why: "Activity events capture distributed tracing spans instrumented by the .NET runtime, HttpClient, EF Core, and custom OpenTelemetry instrumentation.",
            impact: "Slow activities with high error rates indicate bottleneck operations that degrade end-to-end request latency across the distributed system.",
            bullets: [
                "Slow activities     — operations exceeding 1 second",
                "Top operations      — operations by total time and error rate",
                "Error rate timeline — per-second timeline of activity errors"
            ],
            action: "Correlate slow activities with SQL and HTTP traces for the same time window. " +
                    "Add custom Activity.Start/Stop spans around critical code paths for better visibility. " +
                    "Export traces to Jaeger/Zipkin/OTLP for distributed waterfall analysis."
        );

        sink.Section("Trace Summary", "otel-summary");
        sink.KeyValues([
            ("Trace",              data.TraceInfo),
            ("Total activities",   data.TotalActivities.ToString("N0")),
            ("Total errors",       data.TotalErrors > 0
                                    ? $"{data.TotalErrors} ⚠" : "0"),
            ("Slow activities",    data.SlowActivities.Count.ToString("N0")),
            ("Process filter",     data.FilteredProcess ?? "(all processes)"),
        ]);

        if (!data.HasData)
        {
            sink.Alert(AlertLevel.Info, "No Activity/DiagnosticSource events found.",
                "Collect with: --providers 'System.Diagnostics.DiagnosticSource:0xFF:5'",
                "Or enable OpenTelemetry SDK with OTLP exporter and include the DiagnosticSource listener.");
            return;
        }

        if (data.TotalErrors > 0)
            sink.Alert(AlertLevel.Warning,
                $"{data.TotalErrors} activity error(s) detected.",
                "Error activities indicate failed operations in the traced call graph.");

        if (data.ErrorRateTimeline is { Count: > 2 } tl)
            sink.Sparkline(tl, "Activity errors per second", "/s");

        if (data.TopOperations.Count > 0)
        {
            sink.Section("Top Operations", "otel-operations");
            var rows = new List<string[]>(Math.Min(top, data.TopOperations.Count));
            foreach (var op in data.TopOperations.Take(top))
                rows.Add([op.OperationName, op.Count.ToString("N0"), op.ErrorCount.ToString("N0"),
                           $"{op.AvgMs:F1} ms", $"{op.MaxMs:F1} ms",
                           $"{op.TotalMs:F0} ms"]);
            sink.Table(
                ["Operation", "Count", "Errors", "Avg Duration", "Max Duration", "Total Duration"],
                rows, "Operations ordered by count");
        }

        if (data.SlowActivities.Count > 0)
        {
            sink.Section("Slowest Activities", "otel-slow");
            var rows = new List<string[]>(Math.Min(top, data.SlowActivities.Count));
            foreach (var a in data.SlowActivities.Take(top))
                rows.Add([a.OperationName, $"{a.DurationMs:F0} ms",
                           a.IsError ? "Error" : "OK", $"{a.TimeMs:F0} ms"]);
            sink.Table(
                ["Operation", "Duration", "Status", "Start Offset"],
                rows, "Ordered by duration descending");
        }
    }
}
