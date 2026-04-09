using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class WcfChannelsCommand
{
    private const string Help = """
        Usage: DumpDetective wcf-channels <dump-file> [options]

        Options:
          -a, --addresses    Show object addresses
          -o, --output <f>   Write report to file
          -h, --help         Show this help
        """;

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

        var objects = new List<(string Type, ulong Addr, string State)>();
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning WCF objects...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                var name = obj.Type.Name ?? string.Empty;
                if (!name.StartsWith("System.ServiceModel.", StringComparison.OrdinalIgnoreCase)) continue;
                string state = "";
                try
                {
                    int s = -1;
                    try { s = obj.ReadField<int>("_state"); } catch { }
                    try { if (s < 0) s = obj.ReadField<int>("_communicationState"); } catch { }
                    state = s switch { 0 => "Created", 1 => "Opening", 2 => "Opened", 3 => "Closing", 4 => "Closed", 5 => "Faulted", _ => s >= 0 ? s.ToString() : "" };
                }
                catch { }
                objects.Add((name, obj.Address, state));
            }
        });

        var grouped = objects.GroupBy(o => o.Type).OrderByDescending(g => g.Count())
            .Select(g => {
                int faulted = g.Count(o => o.State == "Faulted");
                return new[] { g.Key, g.Count().ToString("N0"), faulted > 0 ? faulted.ToString() : "" };
            }).ToList();

        int faultedTotal = objects.Count(o => o.State == "Faulted");
        sink.Section("WCF Service/Channel Objects");
        if (objects.Count == 0) { sink.Text("No WCF objects found."); return; }
        sink.Table(["Type", "Count", "Faulted"], grouped);

        if (showAddr)
        {
            var rows = objects.Take(100).Select(o => new[] { o.Type, $"0x{o.Addr:X16}", o.State }).ToList();
            sink.Table(["Type", "Address", "State"], rows);
        }

        if (faultedTotal > 0) sink.Alert(AlertLevel.Warning, $"{faultedTotal} faulted WCF channel(s) detected.",
            advice: "Call Abort() on faulted channels and recreate them.");
        sink.KeyValues([("Total WCF objects", objects.Count.ToString("N0")), ("Faulted", faultedTotal.ToString("N0"))]);
    }
}
