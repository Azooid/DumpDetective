using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Analysis.Memory.Consumers;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Memory.Analyzers;

/// <summary>
/// Finds compiler-generated closure display-class instances (&lt;&gt;c__DisplayClass*) on the heap,
/// groups them by their declaring class, and computes BFS-retained size per group.
/// Closures that retain large object graphs (HttpContext, DbContext, etc.) are the most
/// common source of subtle async/LINQ memory leaks.
/// </summary>
public sealed class ClosureCaptureAnalyzer
{
    public ClosureCaptureData Analyze(DumpContext ctx, int top = 30)
    {
        if (!ctx.Heap.CanWalkHeap)
            return new ClosureCaptureData([], 0, 0, 0);

        // Load BFS cache for retained-size computation.
        BfsIndexCache? bfsCache = null;
        if (BfsIndexCache.IsValid(BfsIndexCache.CachePath(ctx.DumpPath), ctx.DumpPath))
        {
            CommandBase.RunStatus("Loading BFS index for closure analysis...", update =>
                bfsCache = ctx.GetOrCreateAnalysis<BfsCacheBox>(() =>
                    new BfsCacheBox(BfsIndexCache.TryLoad(ctx.DumpPath, update))).Cache);
        }

        // Collect all closure display-class objects.
        var byType = new Dictionary<string, (string ClosureType, string Declaring, List<ulong> Addrs, long OwnSize)>
            (StringComparer.Ordinal);

        // Fast path: data was already collected during the main heap walk.
        if (ctx.GetAnalysis<ClosureCaptureConsumerResult>() is { } cached)
        {
            foreach (var (closureType, entry) in cached.ByType)
                byType[closureType] = (closureType, entry.DeclaringType, entry.SampleAddrs, entry.OwnSizeTotal);
        }
        else
        {
        // Slow path: standalone invocation.
        CommandBase.RunStatus("Scanning closures...", update =>
        {
            long scanned = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null) continue;
                string typeName = obj.Type.Name ?? string.Empty;

                // Compiler-generated closure types contain "<>c__DisplayClass" or "<>c" (static closures)
                // State machine types also capture closures but are handled by async-stacks.
                if (!typeName.Contains("<>c__DisplayClass", StringComparison.Ordinal) &&
                    !typeName.Contains("<>c+<", StringComparison.Ordinal)) continue;

                scanned++;
                if ((scanned & 0xFF) == 0 && sw.ElapsedMilliseconds >= 200)
                {
                    update($"Scanning closures \u2014 {scanned:N0} found  \u2022  {byType.Count:N0} types...");
                    sw.Restart();
                }

                long size = (long)obj.Size;
                string declaring = ExtractDeclaringType(typeName);

                if (!byType.TryGetValue(typeName, out var entry))
                    byType[typeName] = entry = (typeName, declaring, [], 0);

                if (entry.Addrs.Count < 5) entry.Addrs.Add(obj.Address);
                byType[typeName] = (entry.ClosureType, entry.Declaring,
                    entry.Addrs, entry.OwnSize + size);
            }
        });
        } // end slow-path else

        // Build result groups with optional BFS retained sizes.
        var groups = new List<ClosureGroup>(byType.Count);
        var visited = new HashSet<int>();
        long nodeCap = bfsCache is not null
            ? Math.Min(500_000L, bfsCache.NodeCount) : 0;

        foreach (var (closureType, (_, declaring, addrs, ownTotal)) in
                 byType.OrderByDescending(kv => kv.Value.OwnSize).Take(top))
        {
            long retained = 0;
            bool isEst = false;

            if (bfsCache is not null && addrs.Count > 0)
            {
                foreach (var addr in addrs.Take(3))
                {
                    visited.Clear();
                    var (ret, est) = bfsCache.ComputeRetained(addr, visited, nodeCap);
                    retained += ret;
                    if (est) isEst = true;
                }
                // Scale to full group
                int n = addrs.Count;
                long instanceCount = byType[closureType].OwnSize > 0
                    ? ownTotal / (byType[closureType].OwnSize / Math.Max(1, n)) : n;
                if (n < (int)Math.Min(instanceCount, 100))
                    retained = (long)(retained * (double)instanceCount / n);
            }

            // Capture field names from the first sample instance.
            var capturedFields = new List<string>();
            if (addrs.Count > 0)
            {
                try
                {
                    var obj = ctx.Heap.GetObject(addrs[0]);
                    if (obj.IsValid && obj.Type is not null)
                        capturedFields.AddRange(
                            obj.Type.Fields
                                .Where(f => !f.Name?.StartsWith('<') ?? false)
                                .Select(f => $"{f.Name}: {f.Type?.Name ?? "?"}"));
                }
                catch { }
            }

            groups.Add(new ClosureGroup(declaring, closureType, addrs.Count,
                ownTotal, retained, isEst, capturedFields));
        }

        long totalOwn = groups.Sum(g => g.OwnSizeTotal);
        long totalRet = groups.Sum(g => g.RetainedSizeTotal);
        return new ClosureCaptureData(groups, byType.Sum(kv => kv.Value.Addrs.Count),
            totalOwn, totalRet);
    }

    private static string ExtractDeclaringType(string closureTypeName)
    {
        // "MyApp.OrderService+<>c__DisplayClass3_0" → "MyApp.OrderService"
        int plus = closureTypeName.IndexOf('+');
        return plus > 0 ? closureTypeName[..plus] : closureTypeName;
    }
}
