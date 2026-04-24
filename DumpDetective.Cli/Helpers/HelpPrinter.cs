using Spectre.Console;
using DumpDetective.Core.Interfaces;

namespace DumpDetective.Cli;

/// <summary>
/// Renders the top-level help panel shown when no arguments are provided
/// or when <c>--help</c> / <c>-h</c> is passed to the root command.
/// </summary>
internal static class HelpPrinter
{
    // Groups are listed in the order they appear in the help panel.
    // Add new .dmp commands to the appropriate group; add new .nettrace commands to traceGroups.
    private static readonly (string Heading, string[] Names)[] s_groups =
    [
        ("Orchestrator",              ["analyze", "trend-analysis"]),
        ("Heap / Memory",             ["heap-stats", "gen-summary", "high-refs", "string-duplicates",
                                       "memory-leak", "heap-fragmentation", "large-objects",
                                       "pinned-objects", "gc-roots", "finalizer-queue",
                                       "handle-table", "static-refs", "weak-refs"]),
        ("Threads / Concurrency",     ["thread-analysis", "thread-pool",
                                       "deadlock-detection", "async-stacks"]),
        ("Exceptions / Diagnostics",  ["exception-analysis", "event-analysis"]),
        ("Infrastructure / Network",  ["http-requests", "connection-pool", "wcf-channels", "timer-leaks"]),
        ("Targeted / Interactive",    ["type-instances", "object-inspect", "module-list"]),
        ("Replay",                    ["render"]),
    ];

    private static readonly (string Heading, string[] Names)[] s_traceGroups =
    [
        ("Threads / Concurrency",     ["threadpool-starvation"]),
    ];

    public static void Print(IEnumerable<ICommand> commands)
    {
        var lookup = commands.ToDictionary(c => c.Name);

        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap());
        grid.AddColumn(new GridColumn().PadLeft(2));

        grid.AddRow("[bold yellow]Usage[/]", "[dim]DumpDetective <command> [[options]][/]");
        grid.AddRow("", "[dim]Run [bold]DumpDetective <command> --help[/] for command-specific options.[/]");

        // ── .dmp / .mdmp commands ─────────────────────────────────────────────
        grid.AddRow("", "");
        grid.AddRow("[bold white on grey] .dmp / .mdmp commands [/]", "");
        foreach (var (heading, names) in s_groups)
        {
            grid.AddRow("", "");
            grid.AddRow($"[bold yellow]{Markup.Escape(heading)}[/]", "");
            foreach (var name in names)
            {
                if (!lookup.TryGetValue(name, out var cmd)) continue;
                grid.AddRow($"  [bold cyan]{Markup.Escape(cmd.Name)}[/]", Markup.Escape(cmd.Description));
            }
        }

        // ── .nettrace commands ────────────────────────────────────────────────
        grid.AddRow("", "");
        grid.AddRow("[bold white on grey] .nettrace commands [/]", "");
        foreach (var (heading, names) in s_traceGroups)
        {
            grid.AddRow("", "");
            grid.AddRow($"[bold yellow]{Markup.Escape(heading)}[/]", "");
            foreach (var name in names)
            {
                if (!lookup.TryGetValue(name, out var cmd)) continue;
                grid.AddRow($"  [bold cyan]{Markup.Escape(cmd.Name)}[/]", Markup.Escape(cmd.Description));
            }
        }

        grid.AddRow("", "");
        grid.AddRow("[bold yellow]Output formats[/]", "[dim].html  .md  .txt  .json  .bin (Brotli-compressed JSON)[/]");
        grid.AddRow("[bold yellow]-o / --output[/]",  "[dim]Repeatable: -o report.html -o report.bin  writes both files[/]");
        grid.AddRow("[bold yellow]--format[/]",        "[dim]Repeatable: --format html --format bin  writes both files[/]");
        grid.AddRow("  [dim]combined[/]",              "[dim]-o report.html --format bin  adds report.bin automatically[/]");
        grid.AddRow("[bold yellow]Default output[/]", "[dim]<dumpname>.html — use --output console to print to terminal[/]");
        grid.AddRow("[bold yellow]Global flags[/]",   "[dim]--debug   print peak memory after run[/]");
        grid.AddRow("[bold yellow]Env vars[/]",        "[dim]DD_DUMP   default dump path when none is given[/]");

        var panel = new Panel(grid)
        {
            Header      = new PanelHeader("[bold] DumpDetective — .NET memory dump analysis tool [/]"),
            Padding     = new Padding(1, 0),
            BorderStyle = Style.Parse("grey"),
        };
        AnsiConsole.Write(panel);
    }
}
