using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class ExceptionsTraceReport
{
    public void Render(ExceptionsTraceData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "First-chance exception analysis — counts and originating call sites for every exception thrown during the trace.",
            why: "Exception construction and stack-unwind are expensive. Thousands of exceptions per second (even caught ones) add measurable CPU overhead and GC pressure from the exception objects.",
            impact: "An exception flood (e.g., using exceptions for control flow, connection resets, parsing failures) can consume significant CPU. It also clutters diagnostics and masks real errors.",
            bullets: [
                "Total thrown   — every exception instance created during the trace",
                "Unique types   — breadth of exception flood (many types vs. a single recurring exception)",
                "Top frame      — the innermost user-code frame that threw the exception"
            ],
            action: "For the top exception type by count, trace the origin back to the call site and eliminate the root cause (e.g., pre-validate instead of try/catch for control flow, retry policies, connection pool health checks)."
        );

        sink.Section("Trace Summary", "exceptions-summary");
        sink.KeyValues([
            ("Trace",            data.TraceInfo),
            ("Total thrown",     data.TotalThrown.ToString("N0")),
            ("Unique types",     data.UniqueTypes.ToString("N0")),
            ("Process filter",   data.FilteredProcess ?? "(all processes)"),
        ]);

        if (data.TotalThrown == 0)
        {
            sink.Alert(AlertLevel.Warning, "No exception events found in trace.",
                "Collect with exception events enabled.",
                "dotnet-trace: use --profile exceptions\ndotnet-trace: add --providers 'Microsoft-Windows-DotNETRuntime:0x8014:5'");
            return;
        }

        if (data.TotalThrown > 10_000)
            sink.Alert(AlertLevel.Critical,
                $"Exception flood: {data.TotalThrown:N0} exceptions in trace.",
                "This many exceptions indicate exceptions are used for control flow or a systematic error is being swallowed.",
                "Find the top type below and eliminate the throw site or switch to a TryXxx pattern.");
        else if (data.TotalThrown > 1_000)
            sink.Alert(AlertLevel.Warning,
                $"{data.TotalThrown:N0} exceptions — elevated but not necessarily a flood.",
                "Review the top types and verify they are expected.");

        // By type
        sink.Section("Exceptions by Type", "exceptions-by-type");
        var typeRows = data.TopTypes.Take(top).Select(t => new[]
        {
            t.ExceptionType,
            t.Count.ToString("N0"),
            TrimMsg(t.FirstMessage, 60),
            TrimFrame(t.TopFrame, 70),
        }).ToList();
        sink.Table(
            ["Exception type", "Count", "First message", "Top call site"],
            typeRows,
            $"Top {typeRows.Count} exception types  |  {data.TotalThrown:N0} total across {data.UniqueTypes} unique types");

        // Most recent events
        sink.Section("Recent Exceptions (most recent first)", "exceptions-events");
        var evRows = data.RecentEvents.Take(top).Select(e => new[]
        {
            $"{e.TimeMs:F3} ms",
            e.ExceptionType,
            TrimMsg(e.Message, 50),
            e.ThreadId.ToString(),
            TrimFrame(e.TopFrame, 70),
        }).ToList();
        sink.Table(
            ["Time in trace", "Type", "Message", "Thread", "Call site"],
            evRows,
            $"Latest {evRows.Count} exceptions");
    }

    private static string TrimMsg(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static string TrimFrame(string s, int max) =>
        s.Length <= max ? s : "…" + s[^(max - 1)..];
}
