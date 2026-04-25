using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Analysis.Consumers;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Reports all live weak GC handles and <c>ConditionalWeakTable</c> instances.
/// Weak handles (<c>WeakShort</c> / <c>WeakLong</c>) are enumerated from the GC handle
/// table; each is checked to see whether its target is still alive (<c>h.Object != 0</c>).
/// ConditionalWeakTable data uses the pre-built <see cref="Consumers.CwtData"/> cache
/// from <c>CollectHeapObjectsCombined</c> when available, avoiding a second heap walk.
/// For each CWT the consumer read the internal <c>_container._entries</c> array length
/// (falling back to <c>_entries</c> for older .NET layouts) as the approximate entry count.
/// </summary>
public sealed class WeakRefsAnalyzer
{
    public WeakRefsData Analyze(DumpContext ctx)
    {
        var handles = ctx.Runtime.EnumerateHandles()
            .Where(h => h.HandleKind is ClrHandleKind.WeakShort or ClrHandleKind.WeakLong)
            .Select(h =>
            {
                bool alive = h.Object != 0;
                var obj    = alive ? ctx.Heap.GetObject(h.Object) : default;
                bool valid = alive && obj.IsValid;
                return new WeakRefItem(
                    h.HandleKind.ToString(),
                    valid,
                    valid ? obj.Type?.Name ?? "?" : "<collected>",
                    h.Object);
            })
            .ToList();

        // Fast path: ConditionalWeakTableConsumer pre-built during CollectHeapObjectsCombined.
        var cwtCached = ctx.GetAnalysis<CwtData>();
        var cwts = cwtCached is not null
            ? cwtCached.Tables
            : ctx.Heap.CanWalkHeap
                ? ScanConditionalWeakTables(ctx)
                : (IReadOnlyList<CwtInstanceInfo>)Array.Empty<CwtInstanceInfo>();

        return new WeakRefsData(handles, cwts);
    }

    private static IReadOnlyList<CwtInstanceInfo> ScanConditionalWeakTables(DumpContext ctx)
    {
        var result = new List<CwtInstanceInfo>();
        CommandBase.RunStatus("Scanning for ConditionalWeakTable...", update =>
        {
            long count = 0;
            var  sw    = System.Diagnostics.Stopwatch.StartNew();
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                count++;
                if ((count & 0x3FFF) == 0 && sw.ElapsedMilliseconds >= 200)
                {
                    update($"Scanning for ConditionalWeakTable \u2014 {count:N0} objects  \u2022  {result.Count} CWT instances found...");
                    sw.Restart();
                }
                var name = obj.Type.Name ?? string.Empty;
                if (!name.StartsWith("System.Runtime.CompilerServices.ConditionalWeakTable",
                        StringComparison.Ordinal)) continue;

                int entryCount = 0;
                try
                {
                    var container = obj.ReadObjectField("_container");
                    if (!container.IsNull && container.IsValid)
                    {
                        var entries = container.ReadObjectField("_entries");
                        if (!entries.IsNull && entries.IsValid && entries.Type?.IsArray == true)
                            entryCount = entries.AsArray().Length;
                    }
                }
                catch { }

                if (entryCount == 0)
                {
                    try
                    {
                        var entries = obj.ReadObjectField("_entries");
                        if (!entries.IsNull && entries.IsValid && entries.Type?.IsArray == true)
                            entryCount = entries.AsArray().Length;
                    }
                    catch { }
                }

                string typeParam = name.Contains('[') ? name[name.IndexOf('[')..] : "";
                result.Add(new CwtInstanceInfo(typeParam, entryCount));
            }
        });
        return result;
    }
}
