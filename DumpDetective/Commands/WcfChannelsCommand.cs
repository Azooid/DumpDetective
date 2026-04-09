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

        sink.Header(
            "Dump Detective — WCF Channels",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var objects = new List<(string Type, ulong Addr, string State, string Endpoint, string FaultReason)>();
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning WCF objects...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                var name = obj.Type.Name ?? string.Empty;
                if (!name.StartsWith("System.ServiceModel.", StringComparison.OrdinalIgnoreCase)) continue;

                string state       = ReadCommunicationState(obj);
                string endpoint    = ReadEndpointAddress(obj);
                string faultReason = ReadFaultReason(obj, state);

                objects.Add((name, obj.Address, state, endpoint, faultReason));
            }
        });

        sink.Section("Summary");
        if (objects.Count == 0) { sink.Text("No WCF objects found."); return; }

        var grouped = objects
            .GroupBy(o => o.Type)
            .OrderByDescending(g => g.Count())
            .ToList();

        // Summary table
        var summaryRows = grouped.Select(g => {
            int faulted  = g.Count(o => o.State == "Faulted");
            int opened   = g.Count(o => o.State == "Opened");
            return new[]
            {
                g.Key,
                g.Count().ToString("N0"),
                opened.ToString("N0"),
                faulted > 0 ? faulted.ToString("N0") : "—",
            };
        }).ToList();
        sink.Table(["Type", "Count", "Opened", "Faulted"], summaryRows);

        int faultedTotal = objects.Count(o => o.State == "Faulted");
        if (faultedTotal > 0)
            sink.Alert(AlertLevel.Warning,
                $"{faultedTotal} faulted WCF channel(s) detected.",
                advice: "Call channel.Abort() on faulted channels — do not call Close() as it throws. Recreate the channel from factory.");

        sink.KeyValues([
            ("Total WCF objects", objects.Count.ToString("N0")),
            ("Faulted",           faultedTotal.ToString("N0")),
        ]);

        // Endpoint breakdown (if any)
        var endpoints = objects
            .Where(o => o.Endpoint.Length > 0)
            .GroupBy(o => o.Endpoint)
            .OrderByDescending(g => g.Count())
            .Take(20)
            .Select(g => new[] { g.Key, g.Count().ToString("N0"), g.Count(o => o.State == "Faulted").ToString("N0") })
            .ToList();
        if (endpoints.Count > 0)
            sink.Table(["Endpoint Address", "Count", "Faulted"], endpoints, "Distinct endpoint addresses");

        // Fault reasons
        var faultGroups = objects
            .Where(o => o.FaultReason.Length > 0)
            .GroupBy(o => o.FaultReason)
            .Select(g => new[] { g.Key, g.Count().ToString("N0") })
            .ToList();
        if (faultGroups.Count > 0)
            sink.Table(["Fault Reason", "Count"], faultGroups);

        if (showAddr)
        {
            var addrRows = objects.Take(200).Select(o => new[]
            {
                $"0x{o.Addr:X16}", o.Type, o.State, o.Endpoint, o.FaultReason,
            }).ToList();
            sink.Table(["Address", "Type", "State", "Endpoint", "Fault Reason"], addrRows);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static string ReadCommunicationState(Microsoft.Diagnostics.Runtime.ClrObject obj)
    {
        int s = -1;
        foreach (var field in new[] { "_state", "_communicationState", "state" })
        {
            try { s = obj.ReadField<int>(field); break; } catch { }
        }
        return s switch
        {
            0 => "Created", 1 => "Opening", 2 => "Opened",
            3 => "Closing", 4 => "Closed",  5 => "Faulted",
            _ => s >= 0 ? s.ToString() : "",
        };
    }

    static string ReadEndpointAddress(Microsoft.Diagnostics.Runtime.ClrObject obj)
    {
        // Try common field paths for endpoint URI
        foreach (var field in new[] { "_remoteAddress", "_via", "_listenUri", "_address" })
        {
            try
            {
                var uriObj = obj.ReadObjectField(field);
                if (uriObj.IsNull || !uriObj.IsValid) continue;
                // Try reading as Uri._string
                try { string? s = uriObj.ReadStringField("_string"); if (!string.IsNullOrEmpty(s)) return s!; } catch { }
                // Try reading as EndpointAddress.Uri
                try
                {
                    var innerUri = uriObj.ReadObjectField("_uri");
                    if (!innerUri.IsNull && innerUri.IsValid)
                    {
                        try { string? s = innerUri.ReadStringField("_string"); if (!string.IsNullOrEmpty(s)) return s!; } catch { }
                    }
                }
                catch { }
            }
            catch { }
        }
        return "";
    }

    static string ReadFaultReason(Microsoft.Diagnostics.Runtime.ClrObject obj, string state)
    {
        if (state != "Faulted") return "";
        foreach (var field in new[] { "_faultReason", "faultReason", "_closedException" })
        {
            try
            {
                string? s = obj.ReadStringField(field);
                if (!string.IsNullOrEmpty(s)) return s!;
            }
            catch { }
            try
            {
                var exObj = obj.ReadObjectField(field);
                if (!exObj.IsNull && exObj.IsValid)
                {
                    string? msg = exObj.ReadStringField("_message");
                    if (!string.IsNullOrEmpty(msg)) return msg!;
                }
            }
            catch { }
        }
        return "";
    }
}
