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
        var modules = ctx.Runtime.EnumerateModules().ToList();

        var rows = modules
            .Select(m => (Path: m.Name ?? m.AssemblyName ?? "<unknown>", Size: (long)(m.MetadataAddress > 0 ? m.Size : 0)))
            .Where(m => filter is null || m.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(m => !appOnly || !IsSystem(m.Path))
            .OrderBy(m => m.Path)
            .Select(m => new[] { Path.GetFileName(m.Path), IsSystem(m.Path) ? "System" : "App", DumpHelpers.FormatSize(m.Size), m.Path })
            .ToList();

        sink.Section("Module List");
        sink.Table(["Assembly", "Kind", "Size", "Path"], rows, $"{rows.Count} module(s)");
        sink.KeyValues([
            ("Total modules",  modules.Count.ToString("N0")),
            ("App modules",    modules.Count(m => !IsSystem(m.Name ?? m.AssemblyName ?? "")).ToString("N0")),
            ("System modules", modules.Count(m =>  IsSystem(m.Name ?? m.AssemblyName ?? "")).ToString("N0")),
        ]);
    }

    static bool IsSystem(string path)
    {
        var fn = Path.GetFileName(path);
        return fn.StartsWith("System.",   StringComparison.OrdinalIgnoreCase) ||
               fn.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
               fn.StartsWith("mscorlib",  StringComparison.OrdinalIgnoreCase);
    }
}
