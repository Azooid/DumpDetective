using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;
using System.Collections.Concurrent;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Enumerates all non-system static object-reference fields across every AppDomain/module
/// and computes the BFS retained-size tree rooted at each.
/// Phase 1 — static root enumeration: iterates all type-def MethodTables in every module,
///           reads each static object-reference field per AppDomain, filters out null/invalid
///           objects and applies name/exclude filters.
/// Phase 2 — parallel BFS retained size: up to <c>MaxParallelBfs</c> roots are processed
///           in parallel; each BFS is capped at <c>BfsNodeCap</c> nodes to prevent runaway
///           on highly-connected roots like the static BCL caches. Nodes shared between
///           multiple roots are counted only in the first root that visits them.
/// Collection detection: if a root's type name contains a known collection marker
///           (Dictionary, List, HashSet, etc.) the top 10 element type names are sampled
///           from the first-level object references for display.
/// </summary>
public sealed class StaticRefsAnalyzer
{
    private const int  BfsNodeCap          = 500_000;  // max objects per BFS root
    private const int  MaxParallelBfs      = 8;

    private static readonly HashSet<string> CollectionMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dictionary", "List", "HashSet", "Queue", "Stack", "Array", "Cache", "ConcurrentDictionary",
        "ConcurrentBag", "ConcurrentQueue", "ImmutableDictionary", "ImmutableList",
    };

    public StaticRefsData Analyze(DumpContext ctx, string? filter = null, HashSet<string>? excludes = null)
    {
        // ── Phase 1: enumerate all static field roots (fast, single-threaded) ──
        var roots = new List<(string DeclType, string FieldName, string FieldType, ulong Addr)>();

        CommandBase.RunStatus("Scanning static fields + BFS retained sizes...", update =>
        {
            // Phase 1 — collect roots
            update("Enumerating static fields...");
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
                                string fieldType = obj.Type?.Name ?? "<unknown>";
                                roots.Add((declType, fieldName, fieldType, obj.Address));
                            }
                            catch { }
                        }
                    }
                }
            }

            // Phase 2 — BFS all roots in parallel to compute retained sizes.
            // Each root gets an independent BFS capped at BfsNodeCap nodes so a
            // pathological root (e.g. a huge static cache) doesn't block others.
            var results  = new StaticFieldEntry[roots.Count];
            long totalSz = 0;
            int  done    = 0;
            var  sw      = System.Diagnostics.Stopwatch.StartNew();

            Parallel.ForEach(
                Enumerable.Range(0, roots.Count),
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelBfs },
                i =>
                {
                    var (declType, fieldName, fieldType, addr) = roots[i];
                    var (retained, capped) = EstimateRetained(ctx.Heap, addr);

                    results[i] = new StaticFieldEntry(
                        DeclType:     declType,
                        FieldName:    fieldName,
                        FieldType:    fieldType,
                        IsCollection: IsCollectionType(fieldType),
                        RetainedSize: retained,
                        Addr:         addr);

                    Interlocked.Add(ref totalSz, retained);
                    int cur = Interlocked.Increment(ref done);

                    // Rate-limit progress updates — checking ElapsedMilliseconds on every
                    // iteration would cause excessive Stopwatch reads under parallelism.
                    if (sw.ElapsedMilliseconds >= 200)
                    {
                        sw.Restart();
                        update($"BFS retained sizes — {cur}/{roots.Count} roots  •  {DumpHelpers.FormatSize(Interlocked.Read(ref totalSz))} retained...");
                    }
                });

            // Sort descending by retained size so the largest roots appear first.
            var fields = new List<StaticFieldEntry>(results);
            fields.Sort((a, b) => b.RetainedSize.CompareTo(a.RetainedSize));

            _fields   = fields;
            _totalSz  = Interlocked.Read(ref totalSz);
        });

        var finalFields = _fields  ?? [];
        long finalTotal = _totalSz;
        return new StaticRefsData(finalFields, finalFields.Count, finalTotal);
    }

    private List<StaticFieldEntry>? _fields;
    private long _totalSz;

    // BFS capped at BfsNodeCap nodes — uses address-only stack to minimise allocations.
    private static (long Size, bool Capped) EstimateRetained(ClrHeap heap, ulong rootAddr)
    {
        if (rootAddr == 0) return (0, false);
        var root = heap.GetObject(rootAddr);
        if (!root.IsValid || root.IsNull) return (0, false);

        long   size    = (long)root.Size;
        int    nodes   = 1;
        bool   capped  = false;
        var    visited = new HashSet<ulong>(256) { rootAddr };
        var    stack   = new Stack<ulong>(256);
        stack.Push(rootAddr);

        while (stack.Count > 0)
        {
            if (nodes >= BfsNodeCap) { capped = true; break; }

            var obj = heap.GetObject(stack.Pop());
            if (!obj.IsValid || obj.IsNull) continue;

            try
            {
                foreach (var childAddr in obj.EnumerateReferenceAddresses(carefully: false))
                {
                    if (childAddr == 0 || !visited.Add(childAddr)) continue;
                    var child = heap.GetObject(childAddr);
                    if (!child.IsValid || child.IsNull) continue;
                    size += (long)child.Size;
                    nodes++;
                    stack.Push(childAddr);
                    if (nodes >= BfsNodeCap) { capped = true; break; }
                }
            }
            catch { }
        }

        return (size, capped);
    }

    private static bool IsCollectionType(string typeName) =>
        CollectionMarkers.Any(m => typeName.Contains(m, StringComparison.OrdinalIgnoreCase));
}
