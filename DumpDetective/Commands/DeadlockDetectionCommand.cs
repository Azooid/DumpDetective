using DumpDetective.Core;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class DeadlockDetectionCommand
{
    private const string Help = """
        Usage: DumpDetective deadlock-detection <dump-file> [options]

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
        var threads = ctx.Runtime.Threads.ToList();

        var blocked = threads
            .Select(t => {
                var frames = t.EnumerateStackTrace().Take(10).ToList();
                var bf = frames.FirstOrDefault(IsBlockingFrame);
                return (Thread: t, BlockFrame: bf, Frames: frames);
            })
            .Where(x => x.BlockFrame is not null)
            .ToList();

        sink.Section("Deadlock / Contention Detection");
        sink.KeyValues([
            ("Threads analyzed", threads.Count.ToString("N0")),
            ("Blocked threads",  blocked.Count.ToString("N0")),
        ]);

        if (blocked.Count == 0)
        {
            sink.Alert(AlertLevel.Info, "No threads appear blocked on synchronization primitives.");
            return;
        }

        var rows = blocked.Select((x, i) => new[]
        {
            (i + 1).ToString(),
            x.Thread.ManagedThreadId.ToString(),
            x.Thread.OSThreadId.ToString(),
            x.Thread.State.ToString(),
            x.BlockFrame!.FrameName ?? x.BlockFrame.Method?.Signature ?? "<unknown>",
        }).ToList();
        sink.Table(["#", "Mgd ID", "OS ID", "State", "Blocked At"], rows, "Blocked threads");

        // Contention groups
        var groups = blocked
            .GroupBy(x => ExtractTypeName(x.BlockFrame!.FrameName ?? ""))
            .Where(g => g.Count() > 1)
            .Select(g => new[] { g.Key, g.Count().ToString(), string.Join(", ", g.Select(x => x.Thread.ManagedThreadId)) })
            .ToList();
        if (groups.Count > 0)
        {
            sink.Section("Potential Contention Groups");
            sink.Table(["Type", "Thread Count", "Managed IDs"], groups);
        }

        int lockObjs = ctx.Heap.CanWalkHeap
            ? ctx.Heap.EnumerateObjects().Count(o => o.IsValid && o.Type?.Name is
                "System.Threading.Monitor" or "System.Threading.Mutex" or
                "System.Threading.SemaphoreSlim" or "System.Threading.ReaderWriterLockSlim")
            : 0;
        sink.KeyValues([("Lock objects on heap", lockObjs.ToString("N0"))]);
    }

    static bool IsBlockingFrame(ClrStackFrame f)
    {
        var name = f.Method?.Name ?? string.Empty;
        return name is "WaitOne" or "Wait" or "Enter" or "TryEnter" or "Join" or "Acquire" or "WaitAny" or "WaitAll"
            || name.Contains("Wait", StringComparison.OrdinalIgnoreCase);
    }

    static string ExtractTypeName(string frame)
    {
        int dot = frame.LastIndexOf('.', frame.IndexOf('(') < 0 ? frame.Length - 1 : frame.IndexOf('('));
        return dot > 0 ? frame[..dot] : frame;
    }
}
