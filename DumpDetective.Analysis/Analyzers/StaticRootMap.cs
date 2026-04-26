using DumpDetective.Core.Runtime;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Address-only static root cache used by analyzers that only need to know whether
/// an object is statically rooted (e.g. <c>EventAnalysisAnalyzer</c>).
/// </summary>
internal sealed class StaticRootAddresses
{
    public HashSet<ulong> Addresses { get; }
    private StaticRootAddresses(HashSet<ulong> addresses)
    {
        Addresses  = addresses;
    }

    internal static StaticRootAddresses Build(DumpContext ctx)
    {
        var addresses = new HashSet<ulong>();

        try
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

        return new StaticRootAddresses(addresses);
    }
}

/// <summary>
/// Rich static-root entry cache used by <c>StaticRefsAnalyzer</c>.
/// Stores the full declaring type / field / target address triples needed to
/// group and report static roots.
/// </summary>
internal sealed class StaticRootEntries
{
    public IReadOnlyList<StaticRootEntry> Entries { get; }

    private StaticRootEntries(List<StaticRootEntry> entries)
    {
        Entries = entries;
    }

    internal static StaticRootEntries Build(DumpContext ctx)
    {
        var entries = new List<StaticRootEntry>(4096);

        try
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

        return new StaticRootEntries(entries);
    }
}

internal sealed record StaticRootEntry(
    string DeclType,
    string FieldName,
    string FieldType,
    ulong  Addr);
