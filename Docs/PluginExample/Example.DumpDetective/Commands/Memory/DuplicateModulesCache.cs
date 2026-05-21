using DumpDetective.Core.Runtime;

namespace Example.DumpDetective;

internal sealed record DuplicateModuleEntry(string SimpleName, string Path);

internal sealed class DuplicateModulesCache
{
    public DuplicateModulesCache(IReadOnlyList<DuplicateModuleEntry> modules)
        => Modules = modules;

    public IReadOnlyList<DuplicateModuleEntry> Modules { get; }

    public static DuplicateModulesCache Build(DumpContext ctx)
    {
        var modules = ctx.Runtime.EnumerateModules()
            .Select(module => new DuplicateModuleEntry(
                GetSimpleName(module.Name ?? module.AssemblyName ?? string.Empty),
                module.Name ?? module.AssemblyName ?? "<dynamic>"))
            .ToList();

        return new DuplicateModulesCache(modules);
    }

    private static string GetSimpleName(string moduleName)
    {
        string fileName = Path.GetFileNameWithoutExtension(moduleName);
        return fileName.Length > 0 ? fileName : "<unknown>";
    }
}