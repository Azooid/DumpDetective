using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class DeadlockReport
{
    public void Render(DeadlockData data, IRenderSink sink)
    {
        sink.Section("Analysis Summary");
        sink.KeyValues([
            ("Threads total",        data.TotalThreadsByRuntime.ToString("N0")),
            ("Blocked threads",      data.Blocked.Count.ToString("N0")),
            ("Named threads found",  data.NamedThreadCount.ToString("N0")),
            ("Unique blocking types",data.Blocked.Select(b => b.BlockType).Distinct().Count().ToString("N0")),
        ]);

        if (data.Blocked.Count == 0)
        {
            sink.Alert(AlertLevel.Info, "No threads appear blocked on synchronization primitives.");
            return;
        }

        if (data.Groups.Count > 0)
        {
            sink.Alert(AlertLevel.Critical,
                $"{data.Groups.Count} contention group(s) — {data.Groups.Sum(g => g.ThreadIds.Count)} threads blocked on the same type.",
                "Multiple threads waiting on the same lock type is the primary deadlock indicator.",
                "Enforce lock ordering, use SemaphoreSlim with CancellationToken timeouts, or convert to async/await.");

            RenderContentionGroups(sink, data);
            RenderWaitForTable(sink, data);
        }
        else
        {
            sink.Alert(AlertLevel.Warning,
                $"{data.Blocked.Count} blocked thread(s) — no shared contention type (single-thread contention or I/O wait).");
        }

        RenderAllBlocked(sink, data);
        RenderStackDetails(sink, data);
    }

    private static void RenderContentionGroups(IRenderSink sink, DeadlockData data)
    {
        sink.Section("Contention Groups");
        var rows = data.Groups.Select(g => new[]
        {
            g.LockType,
            g.ThreadIds.Count.ToString("N0"),
            string.Join(", ", g.ThreadIds.Select(id => $"T{id}")),
            g.TopBlockFrame,
        }).ToList();
        sink.Table(["Lock Type", "Waiting Threads", "Thread IDs", "Blocking Frame"], rows);
    }

    private static void RenderWaitForTable(IRenderSink sink, DeadlockData data)
    {
        sink.Section("Thread → Lock Mapping");
        var rows = data.Blocked.Select(b => new[]
        {
            $"T{b.ManagedId}" + (b.ThreadName is not null ? $" [{b.ThreadName}]" : ""),
            b.BlockType,
            b.BlockFrame.Length > 80 ? b.BlockFrame[..77] + "…" : b.BlockFrame,
        }).ToList();
        sink.Table(["Thread", "Waiting On Type", "Blocking Frame"], rows);
    }

    private static void RenderAllBlocked(IRenderSink sink, DeadlockData data)
    {
        sink.Section("All Blocked Threads");
        var rows = data.Blocked.Select(b =>
        {
            bool inGroup = data.Groups.Any(g => g.ThreadIds.Contains(b.ManagedId));
            return new[]
            {
                $"T{b.ManagedId}", $"0x{b.OSThreadId:X4}", b.ThreadName ?? "",
                b.BlockType, inGroup ? "⚠ GROUP" : "",
            };
        }).ToList();
        sink.Table(["Mgd ID", "OS ID", "Name", "Waiting On", "Flags"], rows);
    }

    private static void RenderStackDetails(IRenderSink sink, DeadlockData data)
    {
        sink.Section("Stack Traces");
        foreach (var b in data.Blocked)
        {
            bool inGroup = data.Groups.Any(g => g.ThreadIds.Contains(b.ManagedId));
            string title = $"T{b.ManagedId}" +
                (b.ThreadName is not null ? $" [{b.ThreadName}]" : "") +
                $"  waiting on: {b.BlockType}" +
                (inGroup ? "  ⚠ GROUP" : "");
            sink.BeginDetails(title, open: inGroup);
            sink.Table(["Frame"], b.StackFrames.Select(f => new[] { f }).ToList());
            sink.EndDetails();
        }
    }
}
