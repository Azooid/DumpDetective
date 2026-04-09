using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class ObjectInspectCommand
{
    private const string Help = """
        Usage: DumpDetective object-inspect <dump-file> --address <hex> [options]

        Options:
          --address, -x <addr>   Object address in hex (required)
          -d, --depth <N>        Depth to recurse into references (default: 1)
          -o, --output <f>       Write report to file
          -h, --help             Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        ulong address = 0; int depth = 1;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--address" or "-x") && i + 1 < args.Length) TryParseHex(args[++i], out address);
            else if ((args[i] is "--depth" or "-d") && i + 1 < args.Length) int.TryParse(args[++i], out depth);
        }
        if (address == 0) { AnsiConsole.MarkupLine("[bold red]✗[/] --address is required."); return 1; }
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, address, depth));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, ulong address, int depth = 1)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);
        var obj = ctx.Heap.GetObject(address);
        if (!obj.IsValid) { sink.Alert(AlertLevel.Warning, $"No valid object at 0x{address:X16}"); return; }

        sink.Section($"Object Inspect: 0x{address:X16}");
        RenderObject(obj, sink, depth, 0);
    }

    static void RenderObject(ClrObject obj, IRenderSink sink, int maxDepth, int depth)
    {
        var type = obj.Type;
        sink.KeyValues([
            ("Type",    type?.Name ?? "<unknown>"),
            ("Address", $"0x{obj.Address:X16}"),
            ("Size",    DumpHelpers.FormatSize((long)obj.Size)),
        ]);

        if (type is null) return;

        if (type.IsString)
        {
            sink.KeyValues([("Value", $"\"{obj.AsString(maxLength: 512)}\"")]);
            return;
        }
        if (type.IsArray)
        {
            sink.KeyValues([("Array length", obj.AsArray().Length.ToString("N0"))]);
            return;
        }

        var fields = type.Fields;
        if (fields.Length == 0) return;

        var rows = new List<string[]>();
        foreach (var field in fields)
        {
            string fn  = field.Name ?? "<unknown>";
            string ft  = field.Type?.Name ?? field.ElementType.ToString();
            string val = "";

            if (field.IsObjectReference)
            {
                try
                {
                    var refObj = obj.ReadObjectField(fn);
                    if (refObj.IsNull) val = "null";
                    else if (refObj.IsValid)
                    {
                        if (refObj.Type?.IsString == true) val = $"\"{refObj.AsString(maxLength: 80)}\"";
                        else val = $"0x{refObj.Address:X16} ({refObj.Type?.Name ?? "?"})";
                    }
                }
                catch { val = "<error>"; }
            }
            else
            {
                try { val = obj.ReadField<long>(fn).ToString(); } catch { }
            }
            rows.Add([fn, ft, val]);
        }
        sink.Table(["Field", "Type", "Value"], rows, $"{fields.Length} field(s)");
    }

    static bool TryParseHex(string s, out ulong value)
    {
        s = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        return ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out value);
    }
}
