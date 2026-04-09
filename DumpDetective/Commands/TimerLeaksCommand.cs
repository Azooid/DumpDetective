using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class TimerLeaksCommand
{
    private const string Help = """
        Usage: DumpDetective timer-leaks <dump-file> [options]

        Options:
          -a, --addresses    Show object addresses
          -o, --output <f>   Write report to file (.md / .html / .txt)
          -h, --help         Show this help
        """;

    private static readonly string[] TimerTypes =
    ["System.Threading.TimerQueueTimer","System.Threading.Timer","System.Timers.Timer","System.Windows.Forms.Timer"];

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;
        bool showAddr = args.Any(a => a is "--addresses" or "-a");
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, showAddr));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, bool showAddr = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);
        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var timers = new List<(string Type, ulong Addr, string Callback)>();
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning timer objects...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                var name = obj.Type.Name ?? string.Empty;
                if (!TimerTypes.Any(t => name.Equals(t, StringComparison.OrdinalIgnoreCase))) continue;
                string cb = "";
                try { var f = obj.ReadObjectField("m_callback"); if (!f.IsNull) cb = f.Type?.Name ?? ""; } catch { }
                timers.Add((name, obj.Address, cb));
            }
        });

        sink.Section("Timer Objects");
        if (timers.Count == 0) { sink.Text("No timer objects found."); return; }

        var grouped = timers.GroupBy(t => t.Type).OrderByDescending(g => g.Count())
            .Select(g => new[] { g.Key, g.Count().ToString("N0") }).ToList();
        sink.Table(["Timer Type", "Count"], grouped);

        if (showAddr)
        {
            var rows = timers.Take(100).Select(t => new[] { t.Type, $"0x{t.Addr:X16}", t.Callback }).ToList();
            sink.Table(["Type", "Address", "Callback Type"], rows);
        }

        if (timers.Count > 500) sink.Alert(AlertLevel.Warning, $"{timers.Count:N0} timer objects detected.",
            advice: "Dispose System.Timers.Timer when no longer needed.");
        sink.KeyValues([("Total timer objects", timers.Count.ToString("N0"))]);
    }
}
