using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Analysis.Memory.Consumers;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Memory.Analyzers;

/// <summary>
/// Lists all loaded CLR modules (assemblies) in the process, classifying each as
/// App, GAC, .NET runtime, or dynamic.
/// Classification is path-based: GAC paths contain <c>\GAC_</c> or <c>assembly\</c>,
/// runtime paths contain the dotnet/shared or Windows/Microsoft.NET patterns,
/// and dynamic modules have no file path.
/// Modules are sorted App-first, then GAC, then runtime/dynamic, then by filename.
/// </summary>
public sealed class ModuleListAnalyzer
{
    public ModuleListData Analyze(DumpContext ctx, string? filter = null, bool appOnly = false)
    {
        var modules = ctx.Runtime.EnumerateModules()
            .Select(m =>
            {
                string path = m.Name ?? m.AssemblyName ?? "<unknown>";
                string fn   = Path.GetFileName(path);
                long   size = m.MetadataAddress > 0 ? (long)m.Size : 0;
                string kind = ModuleKind(path);

                // Collect PDB identity for symbol-server lookups.
                string? pdbPath    = null;
                string? pdbGuid    = null;
                int     pdbAge     = 0;
                bool    pdbPresent = false;
                try
                {
                    var pdb = m.Pdb;
                    if (pdb is not null)
                    {
                        pdbPath    = pdb.Path;
                        pdbGuid    = pdb.Guid.ToString("D");
                        pdbAge     = pdb.Revision;
                        pdbPresent = !string.IsNullOrEmpty(pdb.Path) && File.Exists(pdb.Path);
                    }
                }
                catch { /* PDB metadata may be absent or unreadable */ }

                return new ModuleItem(path, fn, kind, size, pdbPath, pdbGuid, pdbAge, pdbPresent);
            })
            .Where(m => filter is null || m.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(m => !appOnly || m.Kind == "App")
            .OrderBy(m => m.Kind == "App" ? 0 : m.Kind == "GAC" ? 1 : 2)
            .ThenBy(m => m.FileName)
            .ToList();

        var alcs = DetectAssemblyLoadContexts(ctx);
        return new ModuleListData(modules, alcs);
    }

    private static IReadOnlyList<AlcEntry> DetectAssemblyLoadContexts(DumpContext ctx)
    {
        // Fast path: ALC instances already enumerated during the main heap walk.
        if (ctx.GetAnalysis<AlcConsumerResult>() is { } cached)
            return cached.Entries
                .Select(e => new AlcEntry(e.Address, e.Name, e.IsCollectible, e.AssemblyCount))
                .ToList();

        if (!ctx.Heap.CanWalkHeap) return [];
        var result = new List<AlcEntry>();
        try
        {
            // Walk heap looking for AssemblyLoadContext instances
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null) continue;
                string name = obj.Type.Name ?? string.Empty;
                if (!name.EndsWith("AssemblyLoadContext", StringComparison.Ordinal) &&
                    !name.Contains("AssemblyLoadContext", StringComparison.Ordinal)) continue;
                // Skip the static default context wrapper types
                if (name.Contains("DefaultAssemblyLoadContext") ||
                    name.Contains("IndividualTestAssemblyLoadContext")) { }

                string displayName = name;
                bool isCollectible = false;
                int  asmCount = 0;
                try
                {
                    var nameField = obj.Type.GetFieldByName("_name");
                    if (nameField is not null)
                        displayName = obj.ReadStringField("_name") ?? name;
                    var collField = obj.Type.GetFieldByName("_isCollectible");
                    if (collField is not null)
                        isCollectible = collField.Read<bool>(obj, interior: false);
                    var loadedField = obj.Type.GetFieldByName("_loadedAssemblies");
                    if (loadedField is not null)
                    {
                        var listObj = loadedField.ReadObject(obj, interior: false);
                        if (listObj.IsValid)
                        {
                            var cntField = listObj.Type?.GetFieldByName("_size") ??
                                          listObj.Type?.GetFieldByName("_count");
                            if (cntField is not null)
                                asmCount = cntField.Read<int>(listObj, interior: false);
                        }
                    }
                }
                catch { }
                result.Add(new AlcEntry(obj.Address, displayName, isCollectible, asmCount));
                if (result.Count >= 100) break; // safety cap
            }
        }
        catch { }
        return result;
    }

    private static string ModuleKind(string path)
    {
        if (string.IsNullOrEmpty(path)) return "Dynamic";
        if (IsGac(path))    return "GAC";
        if (IsSystem(path)) return "System";
        return "App";
    }

    private static bool IsGac(string path) =>
        path.Contains("\\GAC_MSIL\\",    StringComparison.OrdinalIgnoreCase) ||
        path.Contains("\\GAC_32\\",      StringComparison.OrdinalIgnoreCase) ||
        path.Contains("\\GAC_64\\",      StringComparison.OrdinalIgnoreCase) ||
        path.Contains("\\assembly\\GAC", StringComparison.OrdinalIgnoreCase);

    private static bool IsSystem(string path)
    {
        var fn = Path.GetFileName(path);
        return fn.StartsWith("System.",      StringComparison.OrdinalIgnoreCase) ||
               fn.StartsWith("mscorlib",    StringComparison.OrdinalIgnoreCase) ||
               fn.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("\\dotnet\\",        StringComparison.OrdinalIgnoreCase) ||
               path.Contains("Microsoft.NETCore", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("\\runtime\\",       StringComparison.OrdinalIgnoreCase);
    }
}
