using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class TypeInstancesCommand
{
    private const string Help = """
        Usage: DumpDetective type-instances <dump-file> --type <name> [options]

        Options:
          -t, --type <name>    Type name to search (case-insensitive substring)  [required]
          -n, --top <N>        Max instances to show (default: 50)
          -a, --addresses      Show object addresses
          -o, --output <f>     Write report to file
          -h, --help           Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        string? typeName = null; int top = 50; bool showAddr = false;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--type" or "-t") && i + 1 < args.Length) typeName = args[++i];
            else if ((args[i] is "--top" or "-n") && i + 1 < args.Length) int.TryParse(args[++i], out top);
            else if (args[i] is "--addresses" or "-a") showAddr = true;
        }

        if (typeName is null) { AnsiConsole.MarkupLine("[bold red]✗[/] --type is required."); return 1; }
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, typeName, top, showAddr));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, string typeName, int top = 50, bool showAddr = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);
        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var found = new List<(string Type, ulong Addr, long Size)>();
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start($"Finding instances of '{typeName}'...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                if (obj.Type.Name?.Contains(typeName, StringComparison.OrdinalIgnoreCase) != true) continue;
                found.Add((obj.Type.Name!, obj.Address, (long)obj.Size));
            }
        });

        var byType = found.GroupBy(f => f.Type)
            .OrderByDescending(g => g.Count())
            .Select(g => new[] { g.Key, g.Count().ToString("N0"), DumpHelpers.FormatSize(g.Sum(x => x.Size)) }).ToList();

        sink.Section($"Type Instances: {typeName}");
        if (found.Count == 0) { sink.Text($"No instances of types matching '{typeName}' found."); return; }
        sink.Table(["Type", "Count", "Total Size"], byType);

        if (showAddr)
        {
            var rows = found.Take(top).Select(f => new[] { f.Type, $"0x{f.Addr:X16}", DumpHelpers.FormatSize(f.Size) }).ToList();
            sink.Table(["Type", "Address", "Size"], rows, $"First {Math.Min(top, found.Count)} instances");
        }
        sink.KeyValues([("Total instances", found.Count.ToString("N0")), ("Total size", DumpHelpers.FormatSize(found.Sum(f => f.Size)))]);
    }
}
