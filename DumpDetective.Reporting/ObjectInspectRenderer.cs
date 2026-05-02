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
        HashSet<ulong> finQueue, HashSet<ulong> pinnedAddrs, HashSet<ulong> visited,
        bool computeRetained = false, long retainedCap = 0,
        Action<string>? update = null,
        IReadOnlyDictionary<string, (long Bytes, bool Estimated)>? preRetained = null,
        Func<ClrObject, IReadOnlyDictionary<string, (long Bytes, bool Estimated)>?>? retainedResolver = null)
    {
        if (!obj.IsValid) { sink.Text("  <invalid object>"); return; }
        if (visited.Contains(obj.Address)) { sink.Text($"  <circular reference \u2014 0x{obj.Address:X16}>"); return; }
        visited.Add(obj.Address);

        // For child objects (depth > 0), resolve preRetained via the cache resolver if available.
        if (computeRetained && preRetained is null && retainedResolver is not null && currentDepth > 0)
        {
            update?.Invoke($"depth {currentDepth + 1} \u2014 computing retained for {obj.Type?.Name ?? "?"} @ 0x{obj.Address:X8}...");
            preRetained = retainedResolver(obj);
        }

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

        // Build MT → average size cache from snapshot (avoids heap.GetObject per BFS node).
        // Note: Snapshot is internal to Core; Reporting has no access, so the cache is skipped
        // here — BFS falls back to heap.GetObject() per child, bounded by retainedCap.
        Dictionary<ulong, long>? mtSizeCache = null;

        var rows      = new List<string[]>();
        var refFields = new List<(string Name, ClrObject Ref)>();
        // (field name → retained bytes, isEstimated) — populated when --retained is active.
        var retainedMap = new Dictionary<string, (long Bytes, bool Estimated)>();

        // Shared BFS state across all per-field retained-size walks.
        // A single visited set ensures each heap node is counted under the FIRST field
        // that reaches it (exclusive retained size) — prevents double-counting shared
        // subgraphs (e.g. two fields that both hold a reference into the same large dict).
        var sharedBfsVisited = computeRetained ? new HashSet<ulong>(1024) : null;
        var sharedLocalMisses = computeRetained ? new Dictionary<ulong, long>(256) : null;

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

                        if (computeRetained && !refObj.IsNull && refObj.IsValid)
                        {
                            string fn2 = fn; // capture for closure safety
                            if (preRetained?.TryGetValue(fn2, out var preRet) == true)
                            {
                                // Pre-computed by caller (cache path) — use directly.
                                retainedMap[fn2] = preRet;
                            }
                            else if (preRetained is null)
                            {
                                // No cache — fall back to per-request BFS.
                                int bfsIdx = retainedMap.Count + 1;
                                update?.Invoke($"depth {currentDepth + 1} — BFS retained [{fn2}] (field {bfsIdx}) @ 0x{obj.Address:X8}...");
                                var (retBytes, isEst) = BfsRetained(
                                    ctx.Heap, refObj.Address, sharedBfsVisited!, retainedCap, mtSizeCache, sharedLocalMisses!,
                                    update, fn2);
                                retainedMap[fn2] = (retBytes, isEst);
                            }
                            // preRetained not null but field absent → null/invalid ref, no entry needed
                        }
                    }
                    else val = "<invalid ref>";
                }
                catch { val = "<error>"; }
            }
            else val = ReadPrimitive(obj, field);

            if (computeRetained && retainedMap.TryGetValue(fn, out var ret))
            {
                string retStr = DumpHelpers.FormatSize(ret.Bytes) + (ret.Estimated ? " ~" : "");
                rows.Add([fn, ft, val, retStr]);
            }
            else
            {
                rows.Add([fn, ft, val]);
            }
        }

        if (computeRetained)
        {
            // Sort by retained bytes descending so the heaviest field is first.
            rows.Sort((a, b) =>
            {
                // Parse the retained column (col 3) — strip the trailing " ~" before FormatSize parsing.
                static long ParseRetained(string s)
                {
                    if (string.IsNullOrEmpty(s)) return -1;
                    s = s.TrimEnd(' ', '~');
                    // FormatSize output: e.g. "10.29 GB", "45 MB", "72 B"
                    var m = System.Text.RegularExpressions.Regex.Match(s,
                        @"^([\d,.]+)\s*(B|KB|MB|GB|TB)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!m.Success) return -1;
                    double v = double.Parse(m.Groups[1].Value.Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture);
                    long mul = m.Groups[2].Value.ToUpperInvariant() switch {
                        "KB" => 1024, "MB" => 1024*1024, "GB" => 1024L*1024*1024,
                        "TB" => 1024L*1024*1024*1024, _ => 1 };
                    return (long)(v * mul);
                }
                long ra = a.Length > 3 ? ParseRetained(a[3]) : -1;
                long rb = b.Length > 3 ? ParseRetained(b[3]) : -1;
                return rb.CompareTo(ra);
            });
            sink.Table(["Field", "Type", "Value", "Retained"], rows, $"{fields.Length} field(s) — sorted by retained size");
        }
        else
        {
            sink.Table(["Field", "Type", "Value"], rows, $"{fields.Length} field(s)");
        }

        if (currentDepth + 1 < maxDepth && refFields.Count > 0)
        {
            // When computing retained sizes, only follow the single heaviest ref field to avoid
            // a combinatorial explosion and produce a focused "heaviest path" trace.
            // In normal (non-retained) mode, show all ref fields as before.
            IEnumerable<(string Name, ClrObject Ref)> toFollow;
            if (computeRetained && retainedMap.Count > 0)
            {
                var heaviest = retainedMap
                    .OrderByDescending(kv => kv.Value.Bytes)
                    .FirstOrDefault();
                toFollow = refFields
                    .Where(rf => rf.Name == heaviest.Key)
                    .Take(1);
            }
            else
            {
                toFollow = refFields.Take(10);
            }

            sink.Section($"References from 0x{obj.Address:X16} (depth {currentDepth + 2}/{maxDepth})");
            foreach (var (fname, refObj) in toFollow)
            {
                sink.BeginDetails($"{fname}: {refObj.Type?.Name ?? "?"} @ 0x{refObj.Address:X16}", open: true);
                Render(ctx, refObj, sink, maxDepth, currentDepth + 1, maxArray, finQueue, pinnedAddrs, visited,
                       computeRetained, retainedCap, update, retainedResolver: retainedResolver);
                sink.EndDetails();
            }
        }
    }

    /// <summary>
    /// Simple BFS that estimates the retained size of <paramref name="rootAddr"/>.
    /// Uses <paramref name="mtSizeCache"/> (MethodTable → avg bytes) to avoid calling
    /// <c>heap.GetObject()</c> per child — same technique as StaticRefsAnalyzer.
    /// Returns (bytes, isEstimated). isEstimated is true when <paramref name="nodeCap"/> was hit.
    /// </summary>
    private static (long Size, bool Estimated) BfsRetained(
        ClrHeap heap, ulong rootAddr, HashSet<ulong> visited, long nodeCap,
        Dictionary<ulong, long>? mtSizeCache,
        Dictionary<ulong, long> localMisses,
        Action<string>? update = null, string fieldHint = "")
    {
        int nodesThisBatch = 0;
        if (rootAddr == 0 || !visited.Add(rootAddr)) return (0, false);
        var root = heap.GetObject(rootAddr);
        if (!root.IsValid || root.IsNull) return (0, false);

        long sampledSize  = (long)root.Size;
        int  sampledNodes = 1;
        var  stack        = new Stack<ulong>(64);
        stack.Push(rootAddr);

        while (stack.Count > 0)
        {
            if (nodeCap > 0 && (long)visited.Count >= nodeCap)
            {
                double avg = sampledNodes > 0 ? (double)sampledSize / sampledNodes : 0;
                return (sampledSize + (long)(stack.Count * avg), true);
            }

            var cur = heap.GetObject(stack.Pop());
            if (!cur.IsValid || cur.IsNull) continue;

            try
            {
                foreach (var childAddr in cur.EnumerateReferenceAddresses(carefully: false))
                {
                    if (childAddr == 0 || !visited.Add(childAddr)) continue;

                    long childSize = 0;
                    var childType = heap.GetObjectType(childAddr);
                    if (childType is not null)
                    {
                        // Variable-size types (string, arrays) have a unique size per instance
                        // and cannot be cached by MethodTable — always read from the object.
                        bool variableSize = childType.IsString || childType.IsArray;
                        if (variableSize)
                        {
                            var co = heap.GetObject(childAddr);
                            childSize = co.IsValid ? (long)co.Size : 0;
                        }
                        // Fixed-layout types: check local cache first, then snapshot cache,
                        // then fall back to heap.GetObject and cache the result.
                        else if (!localMisses.TryGetValue(childType.MethodTable, out childSize) &&
                                 (mtSizeCache is null || !mtSizeCache.TryGetValue(childType.MethodTable, out childSize)))
                        {
                            var co = heap.GetObject(childAddr);
                            childSize = co.IsValid ? (long)co.Size : 0;
                            localMisses[childType.MethodTable] = childSize;
                        }
                    }
                    else
                    {
                        var co = heap.GetObject(childAddr);
                        if (!co.IsValid || co.IsNull) continue;
                        childSize = (long)co.Size;
                    }

                    // Always recurse into the child even if its own size is 0 —
                    // it may reference large objects.  Only skip the size contribution.
                    sampledSize  += childSize;
                    if (childSize > 0) sampledNodes++;
                    stack.Push(childAddr);
                    if (++nodesThisBatch >= 25_000)
                    {
                        nodesThisBatch = 0;
                        update?.Invoke($"BFS [{fieldHint}] \u2014 {visited.Count:N0} nodes  \u2022  {DumpHelpers.FormatSize(sampledSize)} retained...");
                    }
                    if (nodeCap > 0 && (long)visited.Count >= nodeCap) break;
                }
            }
            catch { }
        }

        return (sampledSize, false);
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
