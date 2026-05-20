using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;

namespace Example.DumpDetective;

/// <summary>
/// Scans all loaded CLR modules and flags assemblies that appear more than once —
/// exact duplicates (same path, two app domains) or same simple name at different
/// file paths (version conflict).
///
/// Multiple loads of the same assembly are a common root cause of:
///   • TypeLoadException    — two types with the same full name from different loads
///   • MissingMethodException — caller compiled against a different version
///   • Singleton divergence — two "instances" of what should be one static class
/// </summary>
public sealed class DuplicateModulesCommand : ICommand
{
    public string Name                 => "duplicate-modules";
    public string Description          => "Detect assemblies loaded more than once or at conflicting versions.";
    public bool   IncludeInFullAnalyze => true;
    public string Category             => "Example Plugin";
    public CommandKind Kind            => CommandKind.Memory;

    private const string Help = """
        Usage: DumpDetective duplicate-modules <dump.dmp> [options]

        Scans every CLR module in the dump and groups them by simple name.
        Reports any assembly loaded more than once — exact duplicate paths or
        the same name at different file paths (version conflict).

        Version conflicts cause:
          • TypeLoadException      — two types with the same full name from different loads
          • MissingMethodException — caller compiled against a different version of the callee
          • Singleton divergence   — two "instances" of what should be a single static class

        Options:
          -f, --filter <t>  Only show assemblies whose name contains <t>
          --all             Show all assemblies, not just duplicates
          -o, --output <f>  Write report to file (.html / .md / .txt / .json)
          -h, --help        Show this help

        Examples:
          DumpDetective duplicate-modules app.dmp
          DumpDetective duplicate-modules app.dmp --filter Newtonsoft
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;
        var  a   = CliArgs.Parse(args);
        bool all = a.HasFlag("all");
        return CommandBase.Execute(a.DumpPath, a.EffectiveOutputPaths,
            (ctx, sink) => RenderWith(ctx, sink, a.Filter, all));
    }

    public void Render(DumpContext ctx, IRenderSink sink) => RenderWith(ctx, sink, null, false);

    // BuildReport uses the default ICommand implementation via CommandBase.ReportDocBuilder
    // (wired by ReportingBootstrap.Register at startup) — no explicit override needed.

    // ── analysis ─────────────────────────────────────────────────────────────

    private static void RenderWith(DumpContext ctx, IRenderSink sink, string? filter, bool showAll)
    {
        CommandBase.RenderHeader("Duplicate / Conflicting Assemblies", ctx, sink);
        var cache = ctx.GetOrCreateAnalysis<DuplicateModulesCache>(() => DuplicateModulesCache.Build(ctx));

        // Group every loaded module by its simple name (filename without extension).
        // The same DLL loaded from two different paths, or the same name in two
        // app domains, will both appear in the same group.
        var groups = cache.Modules
            .GroupBy(module => module.SimpleName, StringComparer.OrdinalIgnoreCase)
            .Where(g => filter is null || g.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(g => showAll || g.Count() > 1)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToList();

        int conflicts   = groups.Count(g => g.Count() > 1);
        int extraCopies = groups.Where(g => g.Count() > 1).Sum(g => g.Count() - 1);

        sink.KeyValues(
        [
            ("Total modules in dump",      cache.Modules.Count.ToString("N0")),
            ("Assemblies with duplicates", conflicts.ToString("N0")),
            ("Extra copies total",         extraCopies.ToString("N0")),
        ]);

        if (groups.Count == 0)
        {
            sink.Alert(AlertLevel.Info,
                showAll ? "No modules found." : "No duplicate assemblies detected.",
                "Every assembly name appears exactly once in this dump.");
            return;
        }

        if (conflicts > 0)
            sink.Alert(AlertLevel.Warning,
                $"{conflicts} assembly name(s) loaded more than once",
                "Multiple loads of the same assembly often cause TypeLoadException or MissingMethodException.",
                "Audit binding redirects in app.config or run 'dotnet list package --include-transitive'.");

        sink.Section("Assembly Load Count");
        sink.Table(
            ["Assembly", "Copies", "Path(s)"],
            groups.Select(g => new[]
            {
                g.Key,
                g.Count().ToString(),
                string.Join("  |  ", g.Select(module => module.Path).Distinct()),
            }).ToList(),
            caption: $"{groups.Count} entr{(groups.Count == 1 ? "y" : "ies")} shown" +
                     (showAll ? "" : " (duplicates only — pass --all to see every assembly)"));
    }
}
