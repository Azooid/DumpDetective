using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

public sealed class StaticRefsAnalyzer
{
    private static readonly HashSet<string> CollectionMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dictionary", "List", "HashSet", "Queue", "Stack", "Array", "Cache", "ConcurrentDictionary",
        "ConcurrentBag", "ConcurrentQueue", "ImmutableDictionary", "ImmutableList",
    };

    public StaticRefsData Analyze(DumpContext ctx, string? filter = null, HashSet<string>? excludes = null)
    {
        var fields    = new List<StaticFieldEntry>();
        long totalSz  = 0;

        CommandBase.RunStatus("Scanning static fields...", () =>
        {
            foreach (var appDomain in ctx.Runtime.AppDomains)
            {
                foreach (var module in appDomain.Modules)
                {
                    foreach (var (mt, _) in module.EnumerateTypeDefToMethodTableMap())
                    {
                        if (mt == 0) continue;
                        var clrType = ctx.Heap.GetTypeByMethodTable(mt);
                        if (clrType is null) continue;

                        string declType = clrType.Name ?? "<unknown>";
                        if (DumpHelpers.IsSystemType(declType)) continue;
                        if (excludes is not null && excludes.Any(e =>
                            declType.Contains(e, StringComparison.OrdinalIgnoreCase))) continue;

                        foreach (var sf in clrType.StaticFields)
                        {
                            if (!sf.IsObjectReference) continue;
                            string fieldName = sf.Name ?? "<unknown>";
                            if (filter is not null &&
                                !declType.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                                !fieldName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                                continue;

                            try
                            {
                                var obj = sf.ReadObject(appDomain);
                                if (!obj.IsValid || obj.IsNull) continue;

                                string fieldType   = obj.Type?.Name ?? "<unknown>";
                                bool   isCollection = IsCollectionType(fieldType);
                                long   retained    = EstimateRetained(ctx.Heap, obj);
                                totalSz += retained;

                                fields.Add(new StaticFieldEntry(
                                    DeclType:     declType,
                                    FieldName:    fieldName,
                                    FieldType:    fieldType,
                                    IsCollection: isCollection,
                                    RetainedSize: retained,
                                    Addr:         obj.Address));
                            }
                            catch { }
                        }
                    }
                }
            }
        });

        fields.Sort((a, b) => b.RetainedSize.CompareTo(a.RetainedSize));
        return new StaticRefsData(fields, fields.Count, totalSz);
    }

    // BFS to compute retained size by walking the full object graph.
    private static long EstimateRetained(ClrHeap heap, ClrObject root)
    {
        if (!root.IsValid || root.IsNull) return 0;
        long size    = (long)root.Size;
        var  visited = new HashSet<ulong> { root.Address };
        var  queue   = new Queue<ClrObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var obj = queue.Dequeue();
            try
            {
                foreach (var child in obj.EnumerateReferences(carefully: false))
                {
                    if (!child.IsValid || child.IsNull) continue;
                    if (!visited.Add(child.Address)) continue;
                    size += (long)child.Size;
                    queue.Enqueue(child);
                }
            }
            catch { }
        }
        return size;
    }

    private static bool IsCollectionType(string typeName) =>
        CollectionMarkers.Any(m => typeName.Contains(m, StringComparison.OrdinalIgnoreCase));
}
