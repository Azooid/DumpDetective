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

        Inspects a managed object: type, fields, GC generation, finalizer, and pinned status.
        Use --depth to recurse into referenced objects.

        Options:
          --address, -x <addr>    Object address in hex (required)
          -d, --depth <N>         Depth to recurse into references (default: 1, max: 5)
          --max-array <N>         Max array elements to display (default: 10)
          -o, --output <f>        Write report to file
          -h, --help              Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        ulong address = 0; int depth = 1; int maxArray = 10;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--address" or "-x") && i + 1 < args.Length)   TryParseHex(args[++i], out address);
            else if ((args[i] is "--depth" or "-d") && i + 1 < args.Length) int.TryParse(args[++i], out depth);
            else if (args[i] == "--max-array"      && i + 1 < args.Length)  int.TryParse(args[++i], out maxArray);
        }
        if (address == 0) { AnsiConsole.MarkupLine("[bold red]✗[/] --address is required."); return 1; }
        depth = Math.Clamp(depth, 1, 5);
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, address, depth, maxArray));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, ulong address, int depth = 1, int maxArray = 10)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);
        sink.Header(
            "Dump Detective — Object Inspector",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        var obj = ctx.Heap.GetObject(address);
        if (!obj.IsValid)
        {
            sink.Alert(AlertLevel.Warning, $"No valid managed object at 0x{address:X16}",
                "Address may point to unmanaged memory, free space, or an invalid location.");
            return;
        }

        // Build context sets once (expensive, done only at root level)
        var finQueue    = new HashSet<ulong>(ctx.Heap.EnumerateFinalizableObjects().Select(o => o.Address));
        var pinnedAddrs = ctx.Runtime.EnumerateHandles()
            .Where(h => h.IsPinned && h.Object != 0)
            .Select(h => h.Object.Address)
            .ToHashSet();
        var visited = new HashSet<ulong>();

        sink.Section($"Object @ 0x{address:X16}");
        RenderObject(ctx, obj, sink, depth, 0, maxArray, finQueue, pinnedAddrs, visited);
    }

    private static void RenderObject(
        DumpContext ctx, ClrObject obj, IRenderSink sink,
        int maxDepth, int currentDepth,
        int maxArray,
        HashSet<ulong> finQueue, HashSet<ulong> pinnedAddrs,
        HashSet<ulong> visited)
    {
        if (!obj.IsValid) { sink.Text("  <invalid object>"); return; }
        if (visited.Contains(obj.Address)) { sink.Text($"  <circular reference — 0x{obj.Address:X16}>"); return; }
        visited.Add(obj.Address);

        var type = obj.Type;
        string gen = GetGenLabel(ctx, obj.Address);

        var meta = new List<(string, string)>
        {
            ("Type",       type?.Name ?? "<unknown>"),
            ("Address",    $"0x{obj.Address:X16}"),
            ("Size",       DumpHelpers.FormatSize((long)obj.Size)),
            ("Generation", gen),
        };
        if (finQueue.Contains(obj.Address))   meta.Add(("Finalizer queue", "YES — pending finalization"));
        if (pinnedAddrs.Contains(obj.Address)) meta.Add(("Pinned",         "YES — GCHandle.Alloc(Pinned)"));
        sink.KeyValues(meta);

        if (type is null) return;

        // ── String ────────────────────────────────────────────────────────────
        if (type.IsString)
        {
            int len = 0;
            try { len = obj.ReadField<int>("_stringLength"); } catch { }
            string val = obj.AsString(maxLength: 512) ?? "";
            sink.KeyValues([("String value", $"\"{val}\""), ("Length", len.ToString("N0"))]);
            return;
        }

        // ── Array ─────────────────────────────────────────────────────────────
        if (type.IsArray)
        {
            var arr   = obj.AsArray();
            int count = arr.Length;
            string elemTypeName = type.ComponentType?.Name ?? "?";
            sink.KeyValues([
                ("Element type",  elemTypeName),
                ("Array length",  count.ToString("N0")),
            ]);

            if (count > 0 && maxArray > 0)
            {
                bool isObjRef = type.ComponentType?.IsObjectReference == true;
                var elemRows  = new List<string[]>();
                for (int i = 0; i < Math.Min(count, maxArray); i++)
                {
                    string elemVal;
                    if (isObjRef)
                    {
                        try
                        {
                            var elem = arr.GetObjectValue(i);
                            elemVal = elem.IsNull ? "null"
                                : elem.Type?.IsString == true
                                    ? $"\"{elem.AsString(80)}\""
                                    : $"0x{elem.Address:X16} ({elem.Type?.Name ?? "?"})";
                        }
                        catch { elemVal = "<error>"; }
                    }
                    else
                    {
                        elemVal = ReadPrimitiveArrayElement(arr, i, type.ComponentType?.ElementType ?? ClrElementType.Int64);
                    }
                    elemRows.Add([$"[{i}]", elemVal]);
                }
                sink.Table(["Index", "Value"], elemRows,
                    $"First {elemRows.Count} of {count:N0} element(s)");
            }
            return;
        }

        // ── Regular object fields ─────────────────────────────────────────────
        var fields = type.Fields;
        if (fields.Length == 0) return;

        var rows     = new List<string[]>();
        var refFields = new List<(string FieldName, ClrObject RefObj)>();

        foreach (var field in fields)
        {
            string fn  = field.Name ?? "<unknown>";
            string ft  = field.Type?.Name ?? field.ElementType.ToString();
            string val;

            if (field.IsObjectReference)
            {
                try
                {
                    var refObj = obj.ReadObjectField(fn);
                    if (refObj.IsNull)       val = "null";
                    else if (refObj.IsValid)
                    {
                        if (refObj.Type?.IsString == true) val = $"\"{refObj.AsString(maxLength: 80)}\"";
                        else                               val = $"0x{refObj.Address:X16} ({refObj.Type?.Name ?? "?"})";

                        // Collect for recursive display if within depth
                        if (currentDepth + 1 < maxDepth && !visited.Contains(refObj.Address))
                            refFields.Add((fn, refObj));
                    }
                    else val = "<invalid ref>";
                }
                catch { val = "<error>"; }
            }
            else
            {
                val = ReadPrimitive(obj, field);
            }
            rows.Add([fn, ft, val]);
        }

        sink.Table(["Field", "Type", "Value"], rows, $"{fields.Length} field(s)");

        // ── Recursive child objects (depth > 1) ───────────────────────────────
        if (currentDepth + 1 < maxDepth && refFields.Count > 0)
        {
            sink.Section($"References from 0x{obj.Address:X16} (depth {currentDepth + 2}/{maxDepth})");
            foreach (var (fname, refObj) in refFields.Take(10))
            {
                sink.BeginDetails($"{fname}: {refObj.Type?.Name ?? "?"} @ 0x{refObj.Address:X16}", open: false);
                RenderObject(ctx, refObj, sink, maxDepth, currentDepth + 1, maxArray, finQueue, pinnedAddrs, visited);
                sink.EndDetails();
            }
        }
    }

    private static string ReadPrimitive(ClrObject obj, ClrField field)
    {
        string name = field.Name ?? "";
        if (string.IsNullOrEmpty(name)) return "<no name>";
        try
        {
            return field.ElementType switch
            {
                ClrElementType.Boolean  => obj.ReadField<bool>(name).ToString(),
                ClrElementType.Char     => $"'{(char)obj.ReadField<ushort>(name)}'",
                ClrElementType.Int8     => obj.ReadField<sbyte>(name).ToString(),
                ClrElementType.UInt8    => obj.ReadField<byte>(name).ToString(),
                ClrElementType.Int16    => obj.ReadField<short>(name).ToString(),
                ClrElementType.UInt16   => obj.ReadField<ushort>(name).ToString(),
                ClrElementType.Int32    => obj.ReadField<int>(name).ToString("N0"),
                ClrElementType.UInt32   => obj.ReadField<uint>(name).ToString("N0"),
                ClrElementType.Int64    => obj.ReadField<long>(name).ToString("N0"),
                ClrElementType.UInt64   => obj.ReadField<ulong>(name).ToString("N0"),
                ClrElementType.Float    => obj.ReadField<float>(name).ToString("G"),
                ClrElementType.Double   => obj.ReadField<double>(name).ToString("G"),
                ClrElementType.Pointer  => $"0x{obj.ReadField<ulong>(name):X16}",
                ClrElementType.NativeInt  => $"0x{obj.ReadField<long>(name):X}",
                ClrElementType.NativeUInt => $"0x{obj.ReadField<ulong>(name):X}",
                _                       => obj.ReadField<long>(name).ToString(),
            };
        }
        catch { return "<error reading value>"; }
    }

    private static string ReadPrimitiveArrayElement(ClrArray arr, int index, ClrElementType elemType)
    {
        try
        {
            return elemType switch
            {
                ClrElementType.Boolean  => arr.GetValue<bool>(index).ToString(),
                ClrElementType.Char     => $"'{(char)arr.GetValue<ushort>(index)}'",
                ClrElementType.Int8     => arr.GetValue<sbyte>(index).ToString(),
                ClrElementType.UInt8    => arr.GetValue<byte>(index).ToString(),
                ClrElementType.Int16    => arr.GetValue<short>(index).ToString(),
                ClrElementType.UInt16   => arr.GetValue<ushort>(index).ToString(),
                ClrElementType.Int32    => arr.GetValue<int>(index).ToString("N0"),
                ClrElementType.UInt32   => arr.GetValue<uint>(index).ToString("N0"),
                ClrElementType.Int64    => arr.GetValue<long>(index).ToString("N0"),
                ClrElementType.UInt64   => arr.GetValue<ulong>(index).ToString("N0"),
                ClrElementType.Float    => arr.GetValue<float>(index).ToString("G"),
                ClrElementType.Double   => arr.GetValue<double>(index).ToString("G"),
                ClrElementType.Pointer  => $"0x{arr.GetValue<ulong>(index):X16}",
                _                       => arr.GetValue<long>(index).ToString(),
            };
        }
        catch { return "<error>"; }
    }

    private static string GetGenLabel(DumpContext ctx, ulong addr)
    {
        var seg = ctx.Heap.GetSegmentByAddress(addr);
        if (seg is null) return "?";
        return seg.Kind switch
        {
            GCSegmentKind.Large    => "LOH",
            GCSegmentKind.Pinned   => "POH",
            GCSegmentKind.Frozen   => "Frozen",
            GCSegmentKind.Ephemeral => EphemeralGen(seg, addr),
            _                      => "Gen2",
        };
    }

    private static string EphemeralGen(ClrSegment seg, ulong addr)
    {
        if (seg.Generation0.Contains(addr)) return "Gen0";
        if (seg.Generation1.Contains(addr)) return "Gen1";
        return "Gen2";
    }

    private static bool TryParseHex(string s, out ulong value)
    {
        s = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        return ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out value);
    }
}
