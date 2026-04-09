using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class ExceptionAnalysisCommand
{
    private const string Help = """
        Usage: DumpDetective exception-analysis <dump-file> [options]

        Options:
          -n, --top <N>      Top N exception types (default: 20)
          -f, --filter <t>   Only types whose name contains <t>
          -a, --addresses    Show object addresses
          -o, --output <f>   Write report to file (.md / .html / .txt)
          -h, --help         Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        int top = 20; string? filter = null; bool showAddr = false;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--top" or "-n") && i + 1 < args.Length)       int.TryParse(args[++i], out top);
            else if ((args[i] is "--filter" or "-f") && i + 1 < args.Length) filter = args[++i];
            else if (args[i] is "--addresses" or "-a") showAddr = true;
        }
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, top, filter, showAddr));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, int top = 20, string? filter = null, bool showAddr = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);
        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var heapEx = new List<(string Type, ulong Addr, string Msg)>();
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning exceptions...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                if (!DumpHelpers.IsExceptionType(obj.Type)) continue;
                var name = obj.Type.Name ?? "<unknown>";
                if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                string msg = "";
                try { msg = obj.ReadObjectField("_message").AsString(maxLength: 80) ?? ""; } catch { }
                heapEx.Add((name, obj.Address, msg));
            }
        });

        var grouped = heapEx.GroupBy(e => e.Type)
            .OrderByDescending(g => g.Count())
            .Take(top).ToList();

        sink.Section("Exceptions on Heap");
        var summaryRows = grouped.Select(g => new[] { g.Key, g.Count().ToString("N0") }).ToList();
        sink.Table(["Exception Type", "Count"], summaryRows, $"{heapEx.Count} total exception objects");

        if (showAddr && heapEx.Count > 0)
        {
            var addrRows = heapEx.Take(top).Select(e => new[] { e.Type, $"0x{e.Addr:X16}", e.Msg }).ToList();
            sink.Table(["Type", "Address", "Message"], addrRows, "Individual exception objects");
        }

        // Threads with active exceptions
        var threadEx = ctx.Runtime.Threads
            .Where(t => t.CurrentException is not null)
            .Select(t => new[] { t.ManagedThreadId.ToString(), t.CurrentException!.Type?.Name ?? "?", t.CurrentException.Message })
            .ToList();
        if (threadEx.Count > 0)
        {
            sink.Section("Active Thread Exceptions");
            sink.Table(["Managed ID", "Exception Type", "Message"], threadEx);
        }

        sink.KeyValues([
            ("Exception objects on heap",  heapEx.Count.ToString("N0")),
            ("Threads with exception",     threadEx.Count.ToString("N0")),
        ]);
    }
}
