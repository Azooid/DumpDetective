using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Memory.Analyzers;

/// <summary>
/// Builds a classification dashboard of all GC roots by handle kind (Strong,
/// Static, Pinned, Weak, Async-pinned, Dependent, SizedRef, thread stack roots).
/// Groups the top referenced types per root kind and estimates total memory held.
/// </summary>
public sealed class GcRootMapAnalyzer
{
    public GcRootMapData Analyze(DumpContext ctx)
    {
        var kindCounts  = new Dictionary<string, (int Handles, long Size)>(StringComparer.Ordinal);
        var typesByKind = new Dictionary<string, Dictionary<string, (int Count, long Size)>>(StringComparer.Ordinal);
        int totalHandles = 0;

        CommandBase.RunStatus("Enumerating GC handles...", update =>
        {
            foreach (var h in ctx.Runtime.EnumerateHandles())
            {
                if (h.Object == 0) continue;
                totalHandles++;
                string kind = h.HandleKind switch
                {
                    ClrHandleKind.Strong           => "Strong",
                    ClrHandleKind.Pinned           => "Pinned",
                    ClrHandleKind.AsyncPinned      => "AsyncPinned",
                    ClrHandleKind.WeakShort        => "WeakShort",
                    ClrHandleKind.WeakLong         => "WeakLong",
                    ClrHandleKind.Dependent        => "Dependent",
                    ClrHandleKind.SizedRef         => "SizedRef",
                    ClrHandleKind.RefCounted       => "RefCounted",
                    ClrHandleKind.WeakWinRT        => "WeakWinRT",
                    _                              => h.HandleKind.ToString(),
                };

                var obj = ctx.Heap.GetObject(h.Object);
                long size = obj.IsValid ? (long)obj.Size : 0;
                string typeName = obj.IsValid ? (obj.Type?.Name ?? "<unknown>") : "<invalid>";

                ref var kv = ref System.Runtime.InteropServices.CollectionsMarshal
                    .GetValueRefOrAddDefault(kindCounts, kind, out _);
                kv = (kv.Handles + 1, kv.Size + size);

                if (!typesByKind.TryGetValue(kind, out var typeDict))
                    typesByKind[kind] = typeDict = new Dictionary<string, (int, long)>(StringComparer.Ordinal);

                ref var tv = ref System.Runtime.InteropServices.CollectionsMarshal
                    .GetValueRefOrAddDefault(typeDict, typeName, out _);
                tv = (tv.Count + 1, tv.Size + size);
            }
        });

        // Stack roots: enumerate thread stack roots with a time budget to
        // avoid spending minutes on dumps with many threads / huge heaps.
        int stackRootCount = 0;
        bool stackRootsPartial = false;
        const int StackRootTimeBudgetMs = 10_000; // 10 s
        try
        {
            CommandBase.RunStatus("Enumerating stack roots...", () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                foreach (var thread in ctx.Runtime.Threads)
                {
                    if (sw.ElapsedMilliseconds > StackRootTimeBudgetMs)
                    {
                        stackRootsPartial = true;
                        break;
                    }
                    foreach (var root in thread.EnumerateStackRoots())
                    {
                        if (root.Object == 0) continue;
                        stackRootCount++;
                    }
                }
            });
        }
        catch { /* Stack root enumeration can fail on some dumps */ }

        // Merge thread stack root count into kindCounts
        if (!kindCounts.TryGetValue("ThreadStack", out var existing))
            kindCounts["ThreadStack"] = (stackRootCount, 0);
        else
            kindCounts["ThreadStack"] = (stackRootCount, existing.Size);

        var byKind = kindCounts
            .OrderByDescending(kv => kv.Value.Handles + (kv.Key == "ThreadStack" ? kv.Value.Handles : 0))
            .Select(kv => new RootKindSummary(
                kv.Key,
                kv.Key == "ThreadStack" ? 0 : kv.Value.Handles,
                kv.Key == "ThreadStack" ? kv.Value.Handles : 0,
                kv.Value.Size))
            .ToList();

        var topTypes = typesByKind
            .SelectMany(kv => kv.Value
                .OrderByDescending(t => t.Value.Count)
                .Take(5)
                .Select(t => new RootTypeEntry(kv.Key, t.Key, t.Value.Count, t.Value.Size)))
            .OrderByDescending(r => r.Count)
            .ToList();

        long totalMem = kindCounts.Values.Sum(v => v.Size);

        return new GcRootMapData(byKind, topTypes, totalHandles, stackRootCount, totalMem,
            StackRootsPartial: stackRootsPartial);
    }
}
