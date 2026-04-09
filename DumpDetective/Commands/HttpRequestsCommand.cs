using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class HttpRequestsCommand
{
    private const string Help = """
        Usage: DumpDetective http-requests <dump-file> [options]

        Options:
          -a, --addresses    Show object addresses
          -o, --output <f>   Write report to file (.md / .html / .txt)
          -h, --help         Show this help
        """;

    private static readonly string[] HttpTypes =
    [
        "System.Net.Http.HttpRequestMessage",
        "System.Net.Http.HttpResponseMessage",
        "System.Net.HttpWebRequest",
        "System.Net.Http.HttpClient",
        "System.Net.Http.HttpClientHandler",
        "System.Net.Http.SocketsHttpHandler",
    ];

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

        var found = new List<(string Type, ulong Addr, string Extra)>();
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning HTTP objects...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                var name = obj.Type.Name ?? string.Empty;
                if (!HttpTypes.Any(t => name.StartsWith(t, StringComparison.OrdinalIgnoreCase))) continue;
                string extra = "";
                try { extra = obj.ReadObjectField("_requestUri").AsString(maxLength: 120) ?? ""; } catch { }
                found.Add((name, obj.Address, extra));
            }
        });

        sink.Section("HTTP Request Objects");
        if (found.Count == 0) { sink.Text("No HTTP request objects found."); return; }

        var summary = found.GroupBy(f => f.Type).OrderByDescending(g => g.Count())
            .Select(g => new[] { g.Key, g.Count().ToString("N0") }).ToList();
        sink.Table(["Type", "Count"], summary);

        if (showAddr)
        {
            var rows = found.Take(100).Select(f => new[] { f.Type, $"0x{f.Addr:X16}", f.Extra }).ToList();
            sink.Table(["Type", "Address", "URI"], rows);
        }
        sink.KeyValues([("Total HTTP objects", found.Count.ToString("N0"))]);
    }
}
