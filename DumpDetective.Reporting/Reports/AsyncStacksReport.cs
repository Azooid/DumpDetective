using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class AsyncStacksReport
{
    public void Render(AsyncStacksData data, IRenderSink sink, int top = 50, bool showAddr = false)
    {
        int total = data.Entries.Count;

        sink.Section("Summary");
        sink.Explain(
            what: "In-flight async/await state machines at the time the dump was captured. Each state machine " +
                  "represents a method that executed 'await' and is currently suspended waiting for a Task to complete.",
            why:  "A large number of suspended state machines indicates a bottleneck somewhere in the async pipeline. " +
                  "The methods with the highest counts are blocked at the deepest bottleneck. " +
                  "This could be slow I/O, lock contention, thread pool starvation, or a downstream service timeout.",
            bullets:
            [
                "High count of one method → all async continuations are stalling at that exact await point",
                "Awaiting HttpClient/SqlCommand/Stream → downstream I/O is slow or timing out",
                "Awaiting SemaphoreSlim/TaskCompletionSource → explicit throttling or internal serialization",
                "All methods in 'Awaiting' state → none are actively running — thread pool may be starved",
                "Growing count across dumps → continuations accumulating faster than they complete",
            ],
            impact: "A growing async backlog means the application is scheduling more work than it can complete. " +
                    "Under sustained backlog, memory grows (each state machine allocates), latency degrades, " +
                    "and eventually the application becomes unresponsive.",
            action: "Identify the top suspended method. Check what it is awaiting. " +
                    "If awaiting I/O: check downstream service health. " +
                    "If awaiting locks: run 'deadlock-detection <dump>'. " +
                    "If awaiting thread pool: run 'thread-pool-starvation <dump>'.");

        var counts = data.Entries
            .GroupBy(e => (e.Method, e.State))
            .ToDictionary(g => g.Key, g => g.Count());

        sink.KeyValues([
            ("Total state machines",  total.ToString("N0")),
            ("Unique methods",        counts.Keys.Select(k => k.Method).Distinct().Count().ToString("N0")),
            ("Suspended (awaiting)",  counts.Where(kv => kv.Key.State == "Awaiting").Sum(kv => kv.Value).ToString("N0")),
            ("Running",               counts.Where(kv => kv.Key.State == "Running").Sum(kv => kv.Value).ToString("N0")),
            ("Completed / Faulted",   counts.Where(kv => kv.Key.State == "Completed").Sum(kv => kv.Value).ToString("N0")),
        ]);

        if (total == 0) { sink.Text("No async state machines found."); return; }

        int suspended = counts.Where(kv => kv.Key.State == "Awaiting").Sum(kv => kv.Value);
        int running   = counts.Where(kv => kv.Key.State == "Running").Sum(kv => kv.Value);
        int completed = counts.Where(kv => kv.Key.State == "Completed").Sum(kv => kv.Value);

        // State distribution donut
        var stateSegs = new List<(string Label, double Value)>();
        if (suspended > 0) stateSegs.Add(("Awaiting", suspended));
        if (running   > 0) stateSegs.Add(("Running",  running));
        if (completed > 0) stateSegs.Add(("Completed", completed));
        if (stateSegs.Count > 1)
            sink.DonutChart(stateSegs, "Async state machine state distribution",
                $"{total:N0}\nstate machines");

        if (suspended > 1000)
            sink.Alert(AlertLevel.Critical, $"{suspended:N0} async state machines suspended (awaiting).",
                "Investigate task backlog — check thread-pool saturation with thread-pool command.");
        else if (suspended > 100)
            sink.Alert(AlertLevel.Warning, $"{suspended:N0} async state machines suspended.");

        RenderTopTable(sink, counts, total, top);
        RenderStateBreakdown(sink, counts, total);
        RenderRetainedByMethod(sink, data);
        if (showAddr) RenderAddresses(sink, data.Entries);
    }

    private static void RenderRetainedByMethod(IRenderSink sink, AsyncStacksData data)
    {
        if (data.RetainedByMethod is not { Count: > 0 }) return;

        sink.Section("Retained Memory by Suspended Method");
        sink.Explain(
            what: "Each suspended async state machine holds references to its captured variables and continuation context. " +
                  "This section shows the total retained memory attributable to state machines grouped by method name.",
            why:  "A method awaiting a slow operation while holding large captured objects (HttpContext, DbContext, large arrays) " +
                  "multiplies that memory pressure by its instance count. The backlog cost is not just the state machine itself " +
                  "but everything it captures.",
            bullets:
            [
                "Retained >> Own \u00d7 Count \u2192 each instance captures a large external graph",
                "High retained with low count \u2192 a few expensive suspension points",
                "High retained with high count \u2192 a widespread bottleneck capturing large objects",
            ],
            action: "For the top retained method: check what is captured in the lambda closure at the await site. " +
                    "Avoid capturing large objects across awaits — extract the data you need before the await call.");

        var rows = data.RetainedByMethod
            .OrderByDescending(r => r.RetainedSizeTotal)
            .Select(r => new[]
            {
                r.Method.Length > 75 ? r.Method[..75] + "\u2026" : r.Method,
                r.Count.ToString("N0"),
                DumpHelpers.FormatSize(r.OwnSizeTotal),
                r.RetainedSizeTotal > 0
                    ? DumpHelpers.FormatSize(r.RetainedSizeTotal) + (r.IsEstimated ? " ~" : "")
                    : "\u2014",
            })
            .ToList();

        sink.Table(
            ["Suspended Method", "Count", "Own Size", "Retained Size"],
            rows,
            "Retained = BFS-computed retained bytes (sampled instances, then scaled). '~' = estimated.");
    }

    private static void RenderTopTable(IRenderSink sink,
        Dictionary<(string Method, string State), int> counts, int total, int top)
    {
        var rows = counts
            .OrderByDescending(kv => kv.Value)
            .Take(top)
            .Select(kv => new[]
            {
                kv.Key.Method, kv.Key.State,
                kv.Value.ToString("N0"),
                $"{kv.Value * 100.0 / Math.Max(1, total):F1}%",
                kv.Key.State == "Awaiting" ? ClassifyAwait(kv.Key.Method) : "",
            }).ToList();

        sink.Section($"Top {rows.Count} State Machines by Method + State");
        sink.Table(["Method", "State", "Count", "%", "Await Hint"], rows,
            $"Top {rows.Count} of {counts.Count} unique (method, state) combinations");
    }

    private static void RenderStateBreakdown(IRenderSink sink,
        Dictionary<(string Method, string State), int> counts, int total)
    {
        var breakdown = counts
            .GroupBy(kv => kv.Key.State)
            .Select(g => new[] { g.Key, g.Sum(kv => kv.Value).ToString("N0"),
                $"{g.Sum(kv => kv.Value) * 100.0 / Math.Max(1, total):F1}%" })
            .OrderByDescending(r => int.Parse(r[1].Replace(",", "")))
            .ToList();
        sink.Table(["State", "Count", "%"], breakdown, "State distribution");
    }

    private static void RenderAddresses(IRenderSink sink, IReadOnlyList<StateMachineEntry> entries)
    {
        sink.Section("Individual State Machine Addresses");
        var rows = entries.Where(e => e.State == "Awaiting").Take(200)
            .Select(e => new[] { e.Method, e.State, $"0x{e.Addr:X16}" }).ToList();
        sink.Table(["Method", "State", "Address"], rows,
            "Up to 200 suspended instances (use WinDbg !dumpobj <addr>)");
    }

    private static string ClassifyAwait(string method)
    {
        if (method.Contains("Http",        StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Request",     StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Rest",        StringComparison.OrdinalIgnoreCase) ||
            method.StartsWith("System.Net", StringComparison.OrdinalIgnoreCase))
            return "HTTP/Network";
        if (method.Contains("Sql",         StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Query",       StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Database",    StringComparison.OrdinalIgnoreCase) ||
            method.Contains("DbContext",   StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Execute",     StringComparison.OrdinalIgnoreCase) ||
            (method.Contains("Db",         StringComparison.OrdinalIgnoreCase) &&
             !method.Contains("Debug",     StringComparison.OrdinalIgnoreCase)))
            return "Database";
        if (method.Contains("Redis",       StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Cache",       StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Memcache",    StringComparison.OrdinalIgnoreCase))
            return "Cache";
        if (method.Contains("Queue",       StringComparison.OrdinalIgnoreCase) ||
            method.Contains("ServiceBus",  StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Kafka",       StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Message",     StringComparison.OrdinalIgnoreCase))
            return "Messaging";
        if (method.Contains("File",        StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Stream",      StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Read",        StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Write",       StringComparison.OrdinalIgnoreCase))
            return "File/I/O";
        if (method.Contains("Semaphore",   StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Lock",        StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Mutex",       StringComparison.OrdinalIgnoreCase) ||
            method.Contains("WaitAsync",   StringComparison.OrdinalIgnoreCase))
            return "Lock/Sync";
        return "";
    }
}
