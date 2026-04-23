using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class ThreadPoolStarvationReport
{
    public void Render(ThreadPoolStarvationData data, IRenderSink sink, int top = 10)
    {
        sink.Explain(
            what: "Thread pool starvation trace analysis — detects WaitHandleWait events and hill-climbing adjustments from ETW / dotnet-trace captures.",
            why: "Starvation occurs when blocking calls (.Result, .Wait(), Thread.Sleep) occupy all pool threads, starving queued work items.",
            impact: "The pool's hill-climbing algorithm slowly injects threads (1 per ~500 ms), but response times remain high until the pool grows enough to drain the backlog.",
            bullets: ["'Starvation adjustments' = the pool detected starvation and forced a new thread injection", "'WaitHandleWait / MonitorWait' events = a thread-pool thread called .Wait() or .Result", "High thread count + low throughput = classic starvation signature"],
            action: "Replace every Task.Wait() / .Result / .GetAwaiter().GetResult() on thread-pool threads with 'await'. Use async I/O methods throughout the call chain."
        );
        sink.Section("Trace Summary");
        sink.KeyValues([
            ("Trace",                       data.TraceInfo),
            ("Total events",                data.TotalEvents.ToString("N0")),
            ("WaitHandleWait events",        data.WaitEvents.Count.ToString("N0")),
            ("Starvation adjustments",       data.StarvationAdjustmentCount.ToString("N0")),
            ("Thread pool max active",       data.TpMaxActive.ToString("N0")),
            ("Thread pool final active",     data.TpFinalActive.ToString("N0")),
        ]);

        if (data.TotalEvents == 0)
        {
            sink.Alert(AlertLevel.Warning, "No events found in trace.",
                "Ensure the trace was collected with the correct CLR keywords (waithandle, threadPool).",
                "Use: dotnet trace collect --clrevents waithandle --clreventlevel verbose");
            return;
        }

        bool hasStarvation = data.StarvationAdjustmentCount > 0;
        bool hasWaitEvents = data.WaitEvents.Count > 0;

        if (hasStarvation)
            sink.Alert(AlertLevel.Critical,
                $"{data.StarvationAdjustmentCount} ThreadPool Starvation adjustment(s) detected.",
                "The hill-climbing algorithm detected thread starvation and injected new threads.",
                "Convert blocking calls (Task.Wait, .Result, .GetAwaiter().GetResult()) to async/await.");

        if (hasWaitEvents)
            sink.Alert(AlertLevel.Warning,
                $"{data.WaitEvents.Count} WaitHandleWait events — possible sync-over-async.",
                "WaitHandleWait on ThreadPool threads blocks a worker slot and can cascade to starvation.");

        if (!hasStarvation && !hasWaitEvents)
            sink.Alert(AlertLevel.Info, "No starvation signals detected in the trace.");

        RenderWaitEventTable(sink, data, top);
        RenderAdjustments(sink, data);
        RenderEventDistribution(sink, data, top);
    }

    private static void RenderWaitEventTable(IRenderSink sink, ThreadPoolStarvationData data, int top)
    {
        if (data.WaitEvents.Count == 0) return;
        sink.Section("WaitHandle Wait Events");
        var rows = data.WaitEvents.Take(top)
            .Select(e => new[] { e.ThreadId.ToString(), e.WaitSourceName })
            .ToList();
        sink.Table(["Thread ID", "Wait Source"], rows,
            $"Top {rows.Count} of {data.WaitEvents.Count} WaitHandle events  |  MonitorWait = Task.Wait/.Result/.GetAwaiter().GetResult()");
    }

    private static void RenderAdjustments(IRenderSink sink, ThreadPoolStarvationData data)
    {
        if (data.Adjustments.Count == 0) return;
        sink.Section("Thread Pool Adjustments (last 50)");
        var rows = data.Adjustments.Select(a => new[]
        {
            a.Timestamp, a.NewCount.ToString("N0"), a.ReasonName,
            a.AverageThroughput > 0 ? $"{a.AverageThroughput:F2}" : "—",
        }).ToList();
        sink.Table(["Timestamp", "New Thread Count", "Reason", "Avg Throughput"], rows,
            "Starvation in Reason column = hill-climate injected new threads to break stall");
    }

    private static void RenderEventDistribution(IRenderSink sink, ThreadPoolStarvationData data, int top)
    {
        if (data.EventCounts.Count == 0) return;
        sink.Section("Event Distribution");
        var rows = data.EventCounts
            .OrderByDescending(kv => kv.Value)
            .Take(top)
            .Select(kv => new[] { kv.Key, kv.Value.ToString("N0") })
            .ToList();
        sink.Table(["Event Name", "Count"], rows, $"Top {rows.Count} events in trace");
    }
}
