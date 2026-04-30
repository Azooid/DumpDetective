using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Finds all GC root paths to instances of a named type.
/// Two passes:
///   Pass 1 — find all heap objects whose type name contains the search string.
///   Pass 2 — for each matching object, walk all GC root handles and per-thread
///             stack roots to find which ones directly reference the object
///             (direct roots). If <c>noIndirect</c> is false, also walks all
///             objects that reference the target (indirect / referrer scan).
/// Results are capped at <c>maxResults</c> objects to prevent runaway on types
/// with millions of instances (e.g. <c>System.String</c>).
/// </summary>
public sealed class GcRootsAnalyzer
{
    public GcRootsData Analyze(DumpContext ctx, string typeName, int maxResults = 10,
                               bool noIndirect = false, ulong singleAddress = 0)
    {
        var targets     = new List<GcRootTarget>();
        var directRoots = new Dictionary<ulong, List<GcRootInfo>>();
        var referrers   = new Dictionary<ulong, List<ReferrerInfo>>();
        bool capped     = false;

        if (singleAddress != 0)
        {
            // Fast path: address supplied — resolve directly without heap scan.
            CommandBase.RunStatus($"Resolving object at 0x{singleAddress:X}...", () =>
            {
                var obj = ctx.Heap.GetObject(singleAddress);
                if (obj.IsValid && !obj.IsNull && obj.Type is not null)
                {
                    string gen = GetGen(ctx.Heap, obj.Address);
                    targets.Add(new GcRootTarget(obj.Address, obj.Type.Name ?? typeName, (long)obj.Size, gen));
                    directRoots[obj.Address] = new List<GcRootInfo>();
                    referrers[obj.Address]   = new List<ReferrerInfo>();
                }
            });
        }
        else
        {
            // Pass 1: find matching objects by type name.
            CommandBase.RunStatus($"Finding instances of '{typeName}'...", update =>
            {
                long count = 0;
                var  sw    = System.Diagnostics.Stopwatch.StartNew();
                var  total = System.Diagnostics.Stopwatch.StartNew();
                int found  = 0;
                foreach (var obj in ctx.Heap.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                    count++;
                    if ((count & 0x3FFF) == 0 && sw.ElapsedMilliseconds >= 200)
                    {
                        update($"Finding instances \u2014 {count:N0} objects scanned  \u2022  {found} matches...");
                        sw.Restart();
                    }
                    string objType = obj.Type.Name ?? "";
                    if (!objType.Contains(typeName, StringComparison.OrdinalIgnoreCase)) continue;

                    if (found >= maxResults) { capped = true; break; }

                    string gen = GetGen(ctx.Heap, obj.Address);
                    targets.Add(new GcRootTarget(obj.Address, objType, (long)obj.Size, gen));
                    directRoots[obj.Address] = new List<GcRootInfo>();
                    referrers[obj.Address]   = new List<ReferrerInfo>();
                    found++;
                }
                update($"[SCAN]heap|{count}|{total.ElapsedMilliseconds}");
            });
        }

        if (targets.Count == 0)
        {
            var emptyDirect = new Dictionary<ulong, IReadOnlyList<GcRootInfo>>();
            var emptyRef    = new Dictionary<ulong, IReadOnlyList<ReferrerInfo>>();
            return new GcRootsData(typeName, targets, capped, emptyDirect, emptyRef);
        }

        var targetAddrs = targets.Select(t => t.Addr).ToHashSet();

        // Pass 2a: enumerate GC roots
        CommandBase.RunStatus("Enumerating GC roots...", () =>
        {
            foreach (var root in ctx.Runtime.Heap.EnumerateRoots())
            {
                if (!targetAddrs.Contains(root.Object)) continue;
                if (!directRoots.TryGetValue(root.Object, out var list)) continue;
                list.Add(new GcRootInfo(root.RootKind.ToString(), root.Address, null));
            }
        });

        // Pass 2b: optional 1-hop referrers
        if (!noIndirect)
        {
            CommandBase.RunStatus("Building referrer map (1-hop)...", update =>
            {
                long count = 0;
                var  sw    = System.Diagnostics.Stopwatch.StartNew();
                var  total = System.Diagnostics.Stopwatch.StartNew();
                foreach (var obj in ctx.Heap.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                    count++;
                    if ((count & 0x3FFF) == 0 && sw.ElapsedMilliseconds >= 200)
                    {
                        update($"Building referrer map \u2014 {count:N0} objects scanned...");
                        sw.Restart();
                    }
                    try
                    {
                        foreach (var child in obj.EnumerateReferences(carefully: false))
                        {
                            if (!targetAddrs.Contains(child.Address)) continue;
                            if (referrers.TryGetValue(child.Address, out var list))
                                list.Add(new ReferrerInfo(obj.Address, obj.Type.Name ?? "<unknown>"));
                        }
                    }
                    catch { }
                }
                update($"[SCAN]heap|{count}|{total.ElapsedMilliseconds}");
            });
        }

        // Freeze lists to readonly
        var frozenDirect = new Dictionary<ulong, IReadOnlyList<GcRootInfo>>(directRoots.Count);
        foreach (var (k, v) in directRoots) frozenDirect[k] = v;
        var frozenRef = new Dictionary<ulong, IReadOnlyList<ReferrerInfo>>(referrers.Count);
        foreach (var (k, v) in referrers) frozenRef[k] = v;

        return new GcRootsData(typeName, targets, capped, frozenDirect, frozenRef);
    }

    private static string GetGen(ClrHeap heap, ulong addr)
    {
        var seg = heap.GetSegmentByAddress(addr);
        return seg?.Kind switch
        {
            GCSegmentKind.Large   => "LOH",
            GCSegmentKind.Pinned  => "POH",
            GCSegmentKind.Ephemeral =>
                seg.Generation0.Contains(addr) ? "Gen0" :
                seg.Generation1.Contains(addr) ? "Gen1" : "Gen2",
            _ => "Gen2",
        };
    }
}
