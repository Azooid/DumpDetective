using Spectre.Console;
using DumpDetective.Core.Interfaces;

namespace DumpDetective.Cli;

/// <summary>
/// Renders the top-level help panel shown when no arguments are provided
/// or when <c>--help</c> / <c>-h</c> is passed to the root command.
/// </summary>
internal static class HelpPrinter
{
    // Defines display order within the .dmp section.
    // Commands whose category is not listed here appear at the end of the dump section.
    private static readonly string[] s_dumpOrder =
    [
        "Orchestrator",
        "Heap / Memory",
        "Threads / Concurrency",
        "Exceptions / Diagnostics",
        "Infrastructure / Network",
        "Targeted / Interactive",
        "Cache Lifecycle",
        "Replay / Comparison",
    ];

    // Defines display order within the .nettrace / .etl section.
    // Unknown categories (not listed here) fall to the end of the trace section.
    private static readonly string[] s_traceOrder =
    [
        "Orchestrator / Cross-source",
        "CPU & Allocation",
        "GC & Memory",
        "Exceptions & Locks",
        "Threads & Concurrency",
        "JIT & HTTP",
        "SQL & Network",
        "Infrastructure",
        "Observability",
        "Intelligence",
    ];

    public static void Print(IEnumerable<ICommand> commands)
    {
        // Split by Kind first, then group each section by Category
        var memoryByCategory = new Dictionary<string, List<ICommand>>(StringComparer.Ordinal);
        var traceByCategory  = new Dictionary<string, List<ICommand>>(StringComparer.Ordinal);
        foreach (var cmd in commands)
        {
            var bucket = cmd.Kind == CommandKind.Trace ? traceByCategory : memoryByCategory;
            if (!bucket.TryGetValue(cmd.Category, out var list))
                bucket[cmd.Category] = list = [];
            list.Add(cmd);
        }

        // Build the set of plugin-sourced command names for visual distinction.
        var pluginCommandNames = new HashSet<string>(
            CommandRegistry.Plugins.SelectMany(p => p.Commands).Select(c => c.Name),
            StringComparer.Ordinal);

        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap());
        grid.AddColumn(new GridColumn().PadLeft(2));

        grid.AddRow("[bold yellow]Usage[/]", "[dim]DumpDetective <command> [[options]][/]");
        grid.AddRow("", "[dim]Run [bold]DumpDetective <command> --help[/] for command-specific options.[/]");

        // ── .dmp / .mdmp commands ─────────────────────────────────────────────
        grid.AddRow("", "");
        grid.AddRow("[bold white on grey] .dmp / .mdmp commands [/]", "");
        RenderSection(grid, memoryByCategory, s_dumpOrder, pluginCommandNames);

        // ── trace commands (.nettrace / .etl) ─────────────────────────────────
        grid.AddRow("", "");
        grid.AddRow("[bold white on grey] trace commands (.nettrace / .etl) [/]", "");
        RenderSection(grid, traceByCategory, s_traceOrder, pluginCommandNames);

        grid.AddRow("", "");
        grid.AddRow("[bold yellow]Output formats[/]", "[dim].html  .md  .txt  .json  .bin (Brotli-compressed JSON)[/]");
        grid.AddRow("[bold yellow]-o / --output[/]",  "[dim]Repeatable: -o report.html -o report.bin  writes both files[/]");
        grid.AddRow("[bold yellow]--format[/]",        "[dim]Repeatable: --format html --format bin  writes both files[/]");
        grid.AddRow("  [dim]combined[/]",              "[dim]-o report.html --format bin  adds report.bin automatically[/]");
        grid.AddRow("[bold yellow]Default output[/]", "[dim]<dumpname>.html alongside the dump file[/]");
        grid.AddRow("[bold yellow]Global flags[/]",   "[dim]--debug   print peak memory after run[/]");
        grid.AddRow("[bold yellow]Env vars[/]",        "[dim]DD_DUMP   default dump path when none is given[/]");
        if (pluginCommandNames.Count > 0)
        {
            grid.AddRow("", "");
            grid.AddRow("[bold yellow]Legend[/]", $"[bold cyan]  command[/]       built-in");
            grid.AddRow("",                       $"[bold orange1]  ⚠ command[/]  [orange1 dim][[plugin]][/]  third-party — verify source before running");
        }

        // ── loaded plugins ────────────────────────────────────────────────────
        var plugins = CommandRegistry.Plugins;
        if (plugins.Count > 0)
        {
            grid.AddRow("", "");
            grid.AddRow("[bold white on grey] loaded plugins [/]", "");
            foreach (var p in plugins)
            {
                string ver = p.Version is not null ? $" [dim]{Markup.Escape(p.Version)}[/]" : "";
                int count  = p.Commands.Count;
                grid.AddRow(
                    $"  [bold green]{Markup.Escape(p.Name)}[/]{ver}",
                    $"[dim]{count} command{(count == 1 ? "" : "s")}[/]");
            }
        }

        var panel = new Panel(grid)
        {
            Header      = new PanelHeader("[bold] DumpDetective — .NET memory dump analysis tool [/]"),
            Padding     = new Padding(1, 0),
            BorderStyle = Style.Parse("grey"),
        };
        AnsiConsole.Write(panel);
    }

    /// <summary>
    /// Renders one section of the help panel.
    /// Categories appear in <paramref name="order"/> first, then any remaining
    /// categories not listed there are appended at the end.
    /// </summary>
    private static void RenderSection(
        Grid grid,
        Dictionary<string, List<ICommand>> byCategory,
        string[] order,
        HashSet<string> pluginCommandNames)
    {
        var seen = new HashSet<string>(order, StringComparer.Ordinal);

        // Known categories in defined order
        foreach (var heading in order)
        {
            if (!byCategory.TryGetValue(heading, out var cmds)) continue;
            grid.AddRow("", "");
            grid.AddRow($"[bold yellow]{Markup.Escape(heading)}[/]", "");
            foreach (var cmd in cmds)
                AddCommandRow(grid, cmd, pluginCommandNames);
        }

        // Unknown categories appended at the end (new commands get a section automatically)
        foreach (var (heading, cmds) in byCategory)
        {
            if (seen.Contains(heading)) continue;
            grid.AddRow("", "");
            grid.AddRow($"[bold yellow]{Markup.Escape(heading)}[/]", "");
            foreach (var cmd in cmds)
                AddCommandRow(grid, cmd, pluginCommandNames);
        }
    }

    private static void AddCommandRow(Grid grid, ICommand cmd, HashSet<string> pluginCommandNames)
    {
        if (pluginCommandNames.Contains(cmd.Name))
        {
            grid.AddRow(
                $"  [bold orange1]⚠ {Markup.Escape(cmd.Name)}[/]",
                $"[orange1 dim][[plugin]][/] {Markup.Escape(cmd.Description)}");
        }
        else
        {
            grid.AddRow(
                $"  [bold cyan]{Markup.Escape(cmd.Name)}[/]",
                Markup.Escape(cmd.Description));
        }
    }
}

