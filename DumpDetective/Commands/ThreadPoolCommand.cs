using DumpDetective.Core;
using DumpDetective.Output;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class ThreadPoolCommand
{
    private const string Help = """
        Usage: DumpDetective thread-pool <dump-file> [options]

        Options:
          -o, --output <file>  Write report to file (.md / .html / .txt)
          -h, --help           Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);
        var tp = ctx.Runtime.ThreadPool;

        sink.Section("Thread Pool State");
        if (tp is null) { sink.Alert(AlertLevel.Warning, "ThreadPool information not available in this dump."); return; }

        sink.KeyValues([
            ("Min worker threads",  tp.MinThreads.ToString()),
            ("Max worker threads",  tp.MaxThreads.ToString()),
            ("Active workers",      tp.ActiveWorkerThreads.ToString()),
            ("Idle workers",        tp.IdleWorkerThreads.ToString()),
        ]);

        int pct = tp.MaxThreads > 0 ? tp.ActiveWorkerThreads * 100 / tp.MaxThreads : 0;
        if (pct >= 100) sink.Alert(AlertLevel.Critical, $"Thread pool saturated: {tp.ActiveWorkerThreads}/{tp.MaxThreads} workers ({pct}%)",
            advice: "Avoid synchronous blocking calls on thread pool threads (e.g. .Result, .Wait()).");
        else if (pct >= 80) sink.Alert(AlertLevel.Warning, $"Thread pool near capacity: {tp.ActiveWorkerThreads}/{tp.MaxThreads} workers ({pct}%)");

        if (ctx.Heap.CanWalkHeap)
        {
            var workItems = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                var name = obj.Type.Name ?? string.Empty;
                if (IsWorkItem(name)) {
                    workItems.TryGetValue(name, out int c);
                    workItems[name] = c + 1;
                }
            }
            if (workItems.Count > 0)
            {
                sink.Section("Queued Work Items");
                var rows = workItems.OrderByDescending(kv => kv.Value)
                    .Select(kv => new[] { kv.Key, kv.Value.ToString("N0") }).ToList();
                sink.Table(["Type", "Count"], rows);
                sink.KeyValues([("Total work items", workItems.Values.Sum().ToString("N0"))]);
            }
        }
    }

    static bool IsWorkItem(string typeName) =>
        typeName is "System.Threading.QueueUserWorkItemCallback" or
            "System.Threading.QueueUserWorkItemCallbackDefaultContext" or
            "System.Threading.Tasks.Task" ||
        typeName.Contains("WorkItem",   StringComparison.OrdinalIgnoreCase) ||
        typeName.Contains("WorkRequest", StringComparison.OrdinalIgnoreCase);
}
