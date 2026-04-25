using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Analyzers;

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
                return new ModuleItem(path, fn, kind, size);
            })
            .Where(m => filter is null || m.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(m => !appOnly || m.Kind == "App")
            .OrderBy(m => m.Kind == "App" ? 0 : m.Kind == "GAC" ? 1 : 2)
            .ThenBy(m => m.FileName)
            .ToList();

        return new ModuleListData(modules);
    }

    private static string ModuleKind(string path)
    {
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
