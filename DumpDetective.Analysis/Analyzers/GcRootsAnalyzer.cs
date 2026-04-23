using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

public sealed class GcRootsAnalyzer
{
    public GcRootsData Analyze(DumpContext ctx, string typeName, int maxResults = 10, bool noIndirect = false)
    {
        var targets     = new List<GcRootTarget>();
        var directRoots = new Dictionary<ulong, List<GcRootInfo>>();
        var referrers   = new Dictionary<ulong, List<ReferrerInfo>>();
        bool capped     = false;

        // Pass 1: find matching objects
        CommandBase.RunStatus($"Finding instances of '{typeName}'...", update =>
        {
            long count = 0;
            var  sw    = System.Diagnostics.Stopwatch.StartNew();
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
        });

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
            CommandBase.RunStatus("Building referrer map (1-hop)...", () =>
            {
                foreach (var obj in ctx.Heap.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
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
