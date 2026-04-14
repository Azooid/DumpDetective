using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Spectre.Console;

namespace DumpDetective.Commands;

// Scans the heap for System.ServiceModel.* objects. Groups by type, extracts
// communication state, endpoint addresses, binding types, and fault reasons.
// Alerts on faulted channels.
internal static class WcfChannelsCommand
{
    private const string Help = """
        Usage: DumpDetective wcf-channels <dump-file> [options]

        Options:
          -a, --addresses    Show object addresses
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
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

        var objects = ScanWcfObjects(ctx);

        sink.Section("Summary");
        if (objects.Count == 0) { sink.Text("No WCF objects found."); return; }

        RenderSummaryTable(sink, objects);
        RenderEndpoints(sink, objects);
        RenderFaultReasons(sink, objects);
        if (showAddr) RenderAddresses(sink, objects);
    }

    // ── Data gathering ────────────────────────────────────────────────────────

    // Walks the heap for System.ServiceModel.* objects and extracts communication state,
    // endpoint address, binding type, and fault reason for each.
    static List<(string Type, ulong Addr, string State, string Endpoint, string Binding, string FaultReason)>
        ScanWcfObjects(DumpContext ctx)
    {
        var objects = new List<(string Type, ulong Addr, string State, string Endpoint, string Binding, string FaultReason)>();
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning WCF objects...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                var name = obj.Type.Name ?? string.Empty;
                if (!name.StartsWith("System.ServiceModel.", StringComparison.OrdinalIgnoreCase)) continue;

                string state       = ReadCommunicationState(obj);
                string endpoint    = ReadEndpointAddress(obj);
                string binding     = ReadBindingType(obj);
                string faultReason = ReadFaultReason(obj, state);
                objects.Add((name, obj.Address, state, endpoint, binding, faultReason));
            }
        });
        return objects;
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    // Type-level summary table (count, opened, faulted, binding) + faulted-channel alert.
    static void RenderSummaryTable(IRenderSink sink,
        IReadOnlyList<(string Type, ulong Addr, string State, string Endpoint, string Binding, string FaultReason)> objects)
    {
        var grouped = objects.GroupBy(o => o.Type).OrderByDescending(g => g.Count()).ToList();
        var summaryRows = grouped.Select(g =>
        {
            int faulted = g.Count(o => o.State == "Faulted");
            int opened  = g.Count(o => o.State == "Opened");
            string bindings = string.Join(", ",
                g.Select(o => o.Binding).Where(b => b.Length > 0).Distinct().Take(3));
            return new[]
            {
                g.Key,
                g.Count().ToString("N0"),
                opened.ToString("N0"),
                faulted > 0 ? faulted.ToString("N0") : "—",
                bindings.Length > 0 ? bindings : "—",
            };
        }).ToList();
        sink.Table(["Type", "Count", "Opened", "Faulted", "Binding"], summaryRows);

        int faultedTotal = objects.Count(o => o.State == "Faulted");
        if (faultedTotal > 0)
            sink.Alert(AlertLevel.Warning,
                $"{faultedTotal} faulted WCF channel(s) detected.",
                advice: "Call channel.Abort() on faulted channels — do not call Close() as it throws. Recreate the channel from factory.");

        sink.KeyValues([
            ("Total WCF objects", objects.Count.ToString("N0")),
            ("Faulted",           faultedTotal.ToString("N0")),
        ]);
    }

    // Distinct endpoint address table (up to 20 rows) with per-endpoint faulted count.
    static void RenderEndpoints(IRenderSink sink,
        IReadOnlyList<(string Type, ulong Addr, string State, string Endpoint, string Binding, string FaultReason)> objects)
    {
        var endpoints = objects
            .Where(o => o.Endpoint.Length > 0)
            .GroupBy(o => o.Endpoint)
            .OrderByDescending(g => g.Count())
            .Take(20)
            .Select(g => new[] { g.Key, g.Count().ToString("N0"), g.Count(o => o.State == "Faulted").ToString("N0") })
            .ToList();
        if (endpoints.Count > 0)
            sink.Table(["Endpoint Address", "Count", "Faulted"], endpoints, "Distinct endpoint addresses");
    }

    // Fault reason frequency table.
    static void RenderFaultReasons(IRenderSink sink,
        IReadOnlyList<(string Type, ulong Addr, string State, string Endpoint, string Binding, string FaultReason)> objects)
    {
        var faultGroups = objects
            .Where(o => o.FaultReason.Length > 0)
            .GroupBy(o => o.FaultReason)
            .Select(g => new[] { g.Key, g.Count().ToString("N0") })
            .ToList();
        if (faultGroups.Count > 0)
            sink.Table(["Fault Reason", "Count"], faultGroups);
    }

    // Raw address table (capped at 200 rows).
    static void RenderAddresses(IRenderSink sink,
        IReadOnlyList<(string Type, ulong Addr, string State, string Endpoint, string Binding, string FaultReason)> objects)
    {
        var addrRows = objects.Take(200).Select(o => new[]
        {
            $"0x{o.Addr:X16}", o.Type, o.State, o.Binding, o.Endpoint, o.FaultReason,
        }).ToList();
        sink.Table(["Address", "Type", "State", "Binding", "Endpoint", "Fault Reason"], addrRows);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Reads the CommunicationState integer from common backing field names.
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

    // Walks common field chains to find the endpoint URI string.
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

    // Returns empty string for non-faulted objects; otherwise reads the fault reason
    // from common backing fields or from the closed-exception message.
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

    // Reads the binding type name from common field names, stripping the namespace prefix.
    static string ReadBindingType(Microsoft.Diagnostics.Runtime.ClrObject obj)
    {
        // Try reading a binding object from common field names
        foreach (var field in new[] { "_binding", "binding", "_channelFactory" })
        {
            try
            {
                var bindObj = obj.ReadObjectField(field);
                if (bindObj.IsNull || !bindObj.IsValid) continue;
                string? typeName = bindObj.Type?.Name;
                if (!string.IsNullOrEmpty(typeName))
                {
                    // Shorten: "System.ServiceModel.BasicHttpBinding" → "BasicHttpBinding"
                    int dot = typeName.LastIndexOf('.');
                    return dot >= 0 ? typeName[(dot + 1)..] : typeName!;
                }
            }
            catch { }
        }
        // Fallback: infer from the channel type name itself
        var name = obj.Type?.Name ?? string.Empty;
        if (name.Contains("BasicHttp",    StringComparison.OrdinalIgnoreCase)) return "BasicHttpBinding";
        if (name.Contains("NetTcp",       StringComparison.OrdinalIgnoreCase)) return "NetTcpBinding";
        if (name.Contains("WSHttp",       StringComparison.OrdinalIgnoreCase)) return "WSHttpBinding";
        if (name.Contains("NetNamedPipe", StringComparison.OrdinalIgnoreCase)) return "NetNamedPipeBinding";
        if (name.Contains("NetMsmq",      StringComparison.OrdinalIgnoreCase)) return "NetMsmqBinding";
        return "";
    }
}
