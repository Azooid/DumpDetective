using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class ModuleListCommand
{
    private const string Help = """
        Usage: DumpDetective module-list <dump-file> [options]

        Options:
          -f, --filter <t>   Only show modules whose name contains <t>
          --app-only         Only show non-system assemblies
          -o, --output <f>   Write report to file (.md / .html / .txt)
          -h, --help         Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        string? filter = null; bool appOnly = false;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--filter" or "-f") && i + 1 < args.Length) filter = args[++i];
            else if (args[i] == "--app-only") appOnly = true;
        }

        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, filter, appOnly));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, string? filter = null, bool appOnly = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — Module List",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        var modules = ctx.Runtime.EnumerateModules().ToList();

        var rows = modules
            .Select(m =>
            {
                string path = m.Name ?? m.AssemblyName ?? "<unknown>";
                string fn   = Path.GetFileName(path);
                long   size = m.MetadataAddress > 0 ? (long)m.Size : 0;
                string kind = ModuleKind(path);

                return (Path: path, FileName: fn, Kind: kind, Size: size);
            })
            .Where(m => filter is null || m.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(m => !appOnly || m.Kind == "App")
            .OrderBy(m => m.Kind == "App" ? 0 : m.Kind == "GAC" ? 1 : 2)
            .ThenBy(m => m.FileName)
            .ToList();

        // Detect duplicates: same filename loaded from multiple paths
        var duplicates = rows
            .GroupBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        if (duplicates.Count > 0)
            sink.Alert(AlertLevel.Warning,
                $"{duplicates.Count} assembly name(s) loaded from multiple paths.",
                "Duplicate assemblies can cause type identity mismatches and unexpected behavior.",
                "Ensure only one version of each assembly is deployed. Check binding redirects.");

        sink.Section("Loaded Modules");
        sink.Table(
            ["Assembly", "Kind", "Size", "Path"],
            rows.Select(m => new[] { m.FileName, m.Kind, DumpHelpers.FormatSize(m.Size), m.Path }).ToList(),
            $"{rows.Count} module(s)");

        int appCount    = rows.Count(m => m.Kind == "App");
        int systemCount = rows.Count(m => m.Kind == "System");
        int gacCount    = rows.Count(m => m.Kind == "GAC");

        sink.KeyValues([
            ("Total modules",   rows.Count.ToString("N0")),
            ("App modules",     appCount.ToString("N0")),
            ("System modules",  systemCount.ToString("N0")),
            ("GAC modules",     gacCount.ToString("N0")),
            ("Duplicate names", duplicates.Count.ToString("N0")),
        ]);

        if (duplicates.Count > 0)
        {
            sink.Section("Duplicate Assemblies");
            foreach (var dup in duplicates)
            {
                sink.BeginDetails($"{dup.Key}  — {dup.Count()} copies", open: true);
                var dupRows = dup.Select(m => new[] { m.Kind, DumpHelpers.FormatSize(m.Size), m.Path }).ToList();
                sink.Table(["Kind", "Size", "Path"], dupRows);
                sink.EndDetails();
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static string ModuleKind(string path)
    {
        if (IsGac(path))    return "GAC";
        if (IsSystem(path)) return "System";
        return "App";
    }

    static bool IsGac(string path) =>
        path.Contains("\\GAC_MSIL\\",     StringComparison.OrdinalIgnoreCase) ||
        path.Contains("\\GAC_32\\",       StringComparison.OrdinalIgnoreCase) ||
        path.Contains("\\GAC_64\\",       StringComparison.OrdinalIgnoreCase) ||
        path.Contains("\\assembly\\GAC", StringComparison.OrdinalIgnoreCase);

    static bool IsSystem(string path)
    {
        var fn = Path.GetFileName(path);
        return fn.StartsWith("System.",    StringComparison.OrdinalIgnoreCase) ||
               fn.StartsWith("mscorlib",  StringComparison.OrdinalIgnoreCase) ||
               fn.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("\\dotnet\\",  StringComparison.OrdinalIgnoreCase) ||
               path.Contains("Microsoft.NETCore", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("\\runtime\\", StringComparison.OrdinalIgnoreCase);
    }
}
