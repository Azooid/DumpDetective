using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class AsyncStacksCommand
{
    private const string Help = """
        Usage: DumpDetective async-stacks <dump-file> [options]

        Options:
          -f, --filter <t>   Only show state machines whose type contains <t>
          -n, --top <N>      Top N methods (default: 50)
          -o, --output <f>   Write report to file (.md / .html / .txt)
          -h, --help         Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        int top = 50; string? filter = null;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--top" or "-n") && i + 1 < args.Length)       int.TryParse(args[++i], out top);
            else if ((args[i] is "--filter" or "-f") && i + 1 < args.Length) filter = args[++i];
        }

        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, top, filter));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, int top = 50, string? filter = null)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);
        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning async state machines...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                var name = obj.Type.Name ?? string.Empty;
                if (!name.Contains(">d__", StringComparison.Ordinal) && !name.Contains(">D__", StringComparison.Ordinal)) continue;
                var method = ExtractMethod(name);
                if (filter != null && !method.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                counts.TryGetValue(method, out int c);
                counts[method] = c + 1;
            }
        });

        int total = counts.Values.Sum();
        var rows = counts.OrderByDescending(kv => kv.Value).Take(top)
            .Select(kv => new[] { kv.Key, kv.Value.ToString("N0"), $"{kv.Value * 100.0 / Math.Max(1,total):F1}%" }).ToList();

        sink.Section("Async State Machines");
        if (rows.Count == 0) { sink.Text("No suspended async state machines found."); return; }
        sink.Table(["Method", "Count", "%"], rows, $"Top {rows.Count} suspended async methods");
        sink.KeyValues([
            ("Total state machines", total.ToString("N0")),
            ("Unique methods",       counts.Count.ToString("N0")),
        ]);
    }

    static string ExtractMethod(string typeName)
    {
        int lt = typeName.LastIndexOf('<');
        int gt = typeName.IndexOf(">d__", lt < 0 ? 0 : lt, StringComparison.Ordinal);
        if (lt < 0 || gt < 0) return typeName;
        return $"{typeName[..lt].TrimEnd('+')} .{typeName[(lt + 1)..gt]}";
    }
}
