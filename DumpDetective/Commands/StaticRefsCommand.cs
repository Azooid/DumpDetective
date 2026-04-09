using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class StaticRefsCommand
{
    private const string Help = """
        Usage: DumpDetective static-refs <dump-file> [options]

        Options:
          -f, --filter <t>     Only types/fields whose name contains <t>
          -e, --exclude <t>    Exclude types containing <t> (repeatable)
          -a, --addresses      Show object addresses
          -o, --output <f>     Write report to file
          -h, --help           Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        string? filter = null; bool showAddr = false;
        var excludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--filter" or "-f") && i + 1 < args.Length) filter = args[++i];
            else if ((args[i] is "--exclude" or "-e") && i + 1 < args.Length) excludes.Add(args[++i]);
            else if (args[i] is "--addresses" or "-a") showAddr = true;
        }
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, filter, excludes, showAddr));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, string? filter = null, HashSet<string>? excludes = null, bool showAddr = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);
        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var appDomain = ctx.Runtime.AppDomains.FirstOrDefault();
        if (appDomain is null) { sink.Alert(AlertLevel.Warning, "No AppDomain found."); return; }

        var seen    = new HashSet<ulong>();
        var results = new List<string[]>();

        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning static fields...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null) continue;
                if (!seen.Add(obj.Type.MethodTable)) continue;
                var declType = obj.Type.Name ?? "<unknown>";
                if (DumpHelpers.IsSystemType(declType)) continue;
                if (excludes?.Any(e => declType.Contains(e, StringComparison.OrdinalIgnoreCase)) == true) continue;

                foreach (var field in obj.Type.StaticFields)
                {
                    if (!field.IsObjectReference) continue;
                    var fieldName = field.Name ?? "<unknown>";
                    if (filter != null &&
                        !declType.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                        !fieldName.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var value = field.ReadObject(appDomain);
                        if (value.IsNull || !value.IsValid) continue;
                        string valType = value.Type?.Name ?? "?";
                        string addr = showAddr ? $"0x{value.Address:X16}" : "";
                        results.Add([declType, fieldName, valType, DumpHelpers.FormatSize((long)value.Size), addr]);
                    }
                    catch { }
                }
            }
        });

        sink.Section("Non-Null Static Reference Fields");
        if (results.Count == 0) { sink.Text("No non-null static reference fields found."); return; }

        string[] headers = showAddr
            ? ["Declaring Type", "Field", "Value Type", "Size", "Address"]
            : ["Declaring Type", "Field", "Value Type", "Size"];
        var rows = results.Select(r => showAddr ? r : r[..4]).ToList();
        sink.Table(headers, rows, $"{results.Count} static fields");
    }
}
