using DumpDetective.Core.Runtime;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Memory.Analyzers;

/// <summary>
/// Address-only static root cache used by analyzers that only need to know whether
/// an object is statically rooted (e.g. <c>EventAnalysisAnalyzer</c>).
/// </summary>
internal sealed class StaticRootAddresses
{
    public HashSet<ulong> Addresses        { get; }
    public int            SkippedModules   { get; }

    private StaticRootAddresses(HashSet<ulong> addresses, int skippedModules)
    {
        Addresses      = addresses;
        SkippedModules = skippedModules;
    }

    internal static StaticRootAddresses Build(DumpContext ctx)
    {
        // Fast path: load from cache (enumerating static fields is ~200–250s).
        string cachePath = StaticRootsCache.CachePath(ctx.DumpPath);
        if (StaticRootsCache.IsValid(cachePath, ctx.DumpPath))
        {
            var cached = StaticRootsCache.TryLoad(cachePath);
            if (cached is not null) return new StaticRootAddresses(cached, 0);
        }

        var addresses      = new HashSet<ulong>();
        int skippedModules = 0;

        try
        {
            foreach (var appDomain in ctx.Runtime.AppDomains)
            {
                foreach (var module in appDomain.Modules)
                {
                    IReadOnlyList<(ulong, int)> typeDefs;
                    try   { typeDefs = module.EnumerateTypeDefToMethodTableMap().ToList(); }
                    catch { skippedModules++; continue; } // skip modules with corrupt/inconsistent metadata

                    foreach (var (mt, _) in typeDefs)
                    {
                        if (mt == 0) continue;
                        var clrType = ctx.Heap.GetTypeByMethodTable(mt);
                        if (clrType is null) continue;

                        foreach (var sf in clrType.StaticFields)
                        {
                            if (!sf.IsObjectReference) continue;
                            try
                            {
                                var obj = sf.ReadObject(appDomain);
                                if (!obj.IsValid || obj.IsNull) continue;
                                addresses.Add(obj.Address);
                            }
                            catch { }
                        }
                    }
                }
            }
        }
        catch { }

        // Persist for subsequent runs.
        try { StaticRootsCache.Save(cachePath, ctx.DumpPath, addresses); } catch { }

        return new StaticRootAddresses(addresses, skippedModules);
    }
}

/// <summary>
/// Rich static-root entry cache used by <c>StaticRefsAnalyzer</c>.
/// Stores the full declaring type / field / target address triples needed to
/// group and report static roots.
/// </summary>
internal sealed class StaticRootEntries
{
    public IReadOnlyList<StaticRootEntry> Entries        { get; }
    public int                            SkippedModules { get; }

    private StaticRootEntries(List<StaticRootEntry> entries, int skippedModules)
    {
        Entries        = entries;
        SkippedModules = skippedModules;
    }

    internal static StaticRootEntries Build(DumpContext ctx)
    {
        var entries        = new List<StaticRootEntry>(4096);
        int skippedModules = 0;

        try
        {
            foreach (var appDomain in ctx.Runtime.AppDomains)
            {
                foreach (var module in appDomain.Modules)
                {
                    IReadOnlyList<(ulong, int)> typeDefs;
                    try   { typeDefs = module.EnumerateTypeDefToMethodTableMap().ToList(); }
                    catch { skippedModules++; continue; } // skip modules with corrupt/inconsistent metadata

                    foreach (var (mt, _) in typeDefs)
                    {
                        if (mt == 0) continue;
                        var clrType = ctx.Heap.GetTypeByMethodTable(mt);
                        if (clrType is null) continue;

                        string declType = clrType.Name ?? "<unknown>";
                        foreach (var sf in clrType.StaticFields)
                        {
                            if (!sf.IsObjectReference) continue;
                            try
                            {
                                var obj = sf.ReadObject(appDomain);
                                if (!obj.IsValid || obj.IsNull) continue;
                                string fieldName = sf.Name ?? "<unknown>";
                                string fieldType = obj.Type?.Name ?? "<unknown>";
                                entries.Add(new StaticRootEntry(declType, fieldName, fieldType, obj.Address));
                            }
                            catch { }
                        }
                    }
                }
            }
        }
        catch { }

        return new StaticRootEntries(entries, skippedModules);
    }
}

internal sealed record StaticRootEntry(
    string DeclType,
    string FieldName,
    string FieldType,
    ulong  Addr);
