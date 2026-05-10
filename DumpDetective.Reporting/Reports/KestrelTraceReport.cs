using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class KestrelTraceReport
{
    public void Render(KestrelTraceData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "Kestrel web server analysis from Microsoft-AspNetCore-Server-Kestrel events — tracks connection acceptance, rejections, queue pressure, and request errors.",
            why: "Kestrel connection rejection storms indicate that the server is overloaded or misconfigured, causing clients to receive immediate failures instead of waiting.",
            impact: "A rejected connection results in an immediate 503 or TCP RST to the client. Under sustained overload, rejection rate can exceed 100% of new connections.",
            bullets: [
                "Rejected connections — connections dropped by Kestrel (queue full, limits exceeded)",
                "Peak concurrent      — highest simultaneous active connections",
                "Queue pressure       — whether connection queue saturation events fired",
                "Request errors       — middleware-level request faults"
            ],
            action: "Increase Kestrel limits: options.Limits.MaxConcurrentConnections. " +
                    "Enable connection backlog tuning. " +
                    "Deploy behind a load balancer or reverse proxy (NGINX, YARP) to absorb burst connections."
        );

        sink.Section("Trace Summary", "kestrel-summary");
        sink.KeyValues([
            ("Trace",                 data.TraceInfo),
            ("Total connections",     data.TotalConnections.ToString("N0")),
            ("Rejected connections",  data.RejectedConnections > 0
                                       ? $"{data.RejectedConnections} ⚠" : "0"),
            ("Peak concurrent",       data.PeakConcurrentConnections.ToString("N0")),
            ("Request errors",        data.RequestErrors.ToString("N0")),
            ("Queue pressure",        data.QueuePressureDetected ? "Yes ⚠" : "No"),
            ("Process filter",        data.FilteredProcess ?? "(all processes)"),
        ]);

        if (!data.HasData)
        {
            sink.Alert(AlertLevel.Info, "No Kestrel events found.",
                "Collect with: --providers 'Microsoft-AspNetCore-Server-Kestrel:0xFF:5'");
            return;
        }

        if (data.RejectedConnections > 0)
            sink.Alert(AlertLevel.Critical,
                $"{data.RejectedConnections} connection(s) rejected by Kestrel.",
                "Kestrel is dropping connections. Clients receive 503 Service Unavailable.",
                "Increase MaxConcurrentConnections limit or add upstream rate limiting.");

        if (data.QueuePressureDetected)
            sink.Alert(AlertLevel.Warning,
                "Kestrel connection queue pressure detected.",
                "The connection acceptance queue is filling up. Rejection may be imminent.");

        if (data.RequestErrors > 0)
            sink.Alert(AlertLevel.Warning,
                $"{data.RequestErrors} request error(s) in the Kestrel pipeline.",
                "Check application logs for the corresponding exception details.");

        if (data.ConnectionTimeline is { Count: > 2 } tl)
            sink.Sparkline(tl, "Peak concurrent connections over time", "");
    }
}
