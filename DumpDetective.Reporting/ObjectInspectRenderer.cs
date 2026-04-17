using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Reporting;

/// <summary>Renders a single managed object and (optionally) its referenced children.</summary>
public static class ObjectInspectRenderer
{
    public static void Render(
        DumpContext ctx, ClrObject obj, IRenderSink sink,
        int maxDepth, int currentDepth, int maxArray,
        HashSet<ulong> finQueue, HashSet<ulong> pinnedAddrs, HashSet<ulong> visited)
    {
        if (!obj.IsValid) { sink.Text("  <invalid object>"); return; }
        if (visited.Contains(obj.Address)) { sink.Text($"  <circular reference — 0x{obj.Address:X16}>"); return; }
        visited.Add(obj.Address);

        var    type = obj.Type;
        string gen  = GetGenLabel(ctx, obj.Address);

        var meta = new List<(string, string)>
        {
            ("Type",       type?.Name ?? "<unknown>"),
            ("Address",    $"0x{obj.Address:X16}"),
            ("Size",       DumpHelpers.FormatSize((long)obj.Size)),
            ("Generation", gen),
        };
        if (finQueue.Contains(obj.Address))    meta.Add(("Finalizer queue", "YES — pending finalization"));
        if (pinnedAddrs.Contains(obj.Address)) meta.Add(("Pinned",          "YES — GCHandle.Alloc(Pinned)"));
        sink.KeyValues(meta);

        if (type is null) return;

        if (type.IsString)
        {
            int len = 0; try { len = obj.ReadField<int>("_stringLength"); } catch { }
            string val = obj.AsString(maxLength: 512) ?? "";
            sink.KeyValues([("String value", $"\"{val}\""), ("Length", len.ToString("N0"))]);
            return;
        }

        if (type.IsArray)
        {
            var arr   = obj.AsArray();
            int count = arr.Length;
            sink.KeyValues([("Element type", type.ComponentType?.Name ?? "?"), ("Array length", count.ToString("N0"))]);

            if (count > 0 && maxArray > 0)
            {
                bool isObjRef = type.ComponentType?.IsObjectReference == true;
                var  elemRows = new List<string[]>();
                for (int i = 0; i < Math.Min(count, maxArray); i++)
                {
                    string elemVal;
                    if (isObjRef)
                    {
                        try
                        {
                            var elem = arr.GetObjectValue(i);
                            elemVal = elem.IsNull ? "null"
                                : elem.Type?.IsString == true ? $"\"{elem.AsString(80)}\""
                                : $"0x{elem.Address:X16} ({elem.Type?.Name ?? "?"})";
                        }
                        catch { elemVal = "<error>"; }
                    }
                    else elemVal = ReadPrimitiveElem(arr, i, type.ComponentType?.ElementType ?? ClrElementType.Int64);
                    elemRows.Add([$"[{i}]", elemVal]);
                }
                sink.Table(["Index", "Value"], elemRows, $"First {elemRows.Count} of {count:N0}");
            }
            return;
        }

        var fields = type.Fields;
        if (fields.Length == 0) return;

        var rows      = new List<string[]>();
        var refFields = new List<(string Name, ClrObject Ref)>();

        foreach (var field in fields)
        {
            string fn = field.Name ?? "<unknown>";
            string ft = field.Type?.Name ?? field.ElementType.ToString();
            string val;

            if (field.IsObjectReference)
            {
                try
                {
                    var refObj = obj.ReadObjectField(fn);
                    if      (refObj.IsNull)    val = "null";
                    else if (refObj.IsValid)
                    {
                        val = refObj.Type?.IsString == true
                            ? $"\"{refObj.AsString(maxLength: 80)}\""
                            : $"0x{refObj.Address:X16} ({refObj.Type?.Name ?? "?"})";
                        if (currentDepth + 1 < maxDepth && !visited.Contains(refObj.Address))
                            refFields.Add((fn, refObj));
                    }
                    else val = "<invalid ref>";
                }
                catch { val = "<error>"; }
            }
            else val = ReadPrimitive(obj, field);

            rows.Add([fn, ft, val]);
        }

        sink.Table(["Field", "Type", "Value"], rows, $"{fields.Length} field(s)");

        if (currentDepth + 1 < maxDepth && refFields.Count > 0)
        {
            sink.Section($"References from 0x{obj.Address:X16} (depth {currentDepth + 2}/{maxDepth})");
            foreach (var (fname, refObj) in refFields.Take(10))
            {
                sink.BeginDetails($"{fname}: {refObj.Type?.Name ?? "?"} @ 0x{refObj.Address:X16}", open: false);
                Render(ctx, refObj, sink, maxDepth, currentDepth + 1, maxArray, finQueue, pinnedAddrs, visited);
                sink.EndDetails();
            }
        }
    }

    private static string GetGenLabel(DumpContext ctx, ulong addr)
    {
        var seg = ctx.Heap.GetSegmentByAddress(addr);
        return seg?.Kind switch
        {
            GCSegmentKind.Large    => "LOH",
            GCSegmentKind.Pinned   => "POH",
            GCSegmentKind.Frozen   => "Frozen",
            GCSegmentKind.Ephemeral =>
                seg.Generation0.Contains(addr) ? "Gen0" :
                seg.Generation1.Contains(addr) ? "Gen1" : "Gen2",
            _ => "Gen2",
        };
    }

    private static string ReadPrimitive(ClrObject obj, ClrField field)
    {
        string name = field.Name ?? "";
        if (name.Length == 0) return "<no name>";
        try
        {
            return field.ElementType switch
            {
                ClrElementType.Boolean  => obj.ReadField<bool>(name).ToString(),
                ClrElementType.Char     => $"'{(char)obj.ReadField<ushort>(name)}'",
                ClrElementType.Int32    => obj.ReadField<int>(name).ToString("N0"),
                ClrElementType.UInt32   => obj.ReadField<uint>(name).ToString("N0"),
                ClrElementType.Int64    => obj.ReadField<long>(name).ToString("N0"),
                ClrElementType.UInt64   => obj.ReadField<ulong>(name).ToString("N0"),
                ClrElementType.Float    => obj.ReadField<float>(name).ToString("G"),
                ClrElementType.Double   => obj.ReadField<double>(name).ToString("G"),
                ClrElementType.Pointer  => $"0x{obj.ReadField<ulong>(name):X16}",
                _ => obj.ReadField<long>(name).ToString(),
            };
        }
        catch { return "<error>"; }
    }

    private static string ReadPrimitiveElem(ClrArray arr, int i, ClrElementType t)
    {
        try
        {
            return t switch
            {
                ClrElementType.Boolean  => arr.GetValue<bool>(i).ToString(),
                ClrElementType.Int32    => arr.GetValue<int>(i).ToString("N0"),
                ClrElementType.Int64    => arr.GetValue<long>(i).ToString("N0"),
                ClrElementType.UInt64   => arr.GetValue<ulong>(i).ToString("N0"),
                ClrElementType.Float    => arr.GetValue<float>(i).ToString("G"),
                ClrElementType.Double   => arr.GetValue<double>(i).ToString("G"),
                ClrElementType.Pointer  => $"0x{arr.GetValue<ulong>(i):X16}",
                _ => arr.GetValue<long>(i).ToString(),
            };
        }
        catch { return "<error>"; }
    }
}
