using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

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

        var cwts = ctx.Heap.CanWalkHeap
            ? ScanConditionalWeakTables(ctx)
            : (IReadOnlyList<CwtInstanceInfo>)Array.Empty<CwtInstanceInfo>();

        return new WeakRefsData(handles, cwts);
    }

    private static IReadOnlyList<CwtInstanceInfo> ScanConditionalWeakTables(DumpContext ctx)
    {
        var result = new List<CwtInstanceInfo>();
        CommandBase.RunStatus("Scanning for ConditionalWeakTable...", () =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
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
