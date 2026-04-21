using System.Diagnostics;
using System.Text;
using DumpDetective.Cli;
using DumpDetective.Reporting;
using Spectre.Console;

Console.OutputEncoding = Encoding.UTF8;
ReportingBootstrap.Register();
DumpDetective.Core.Utilities.CommandBase.FullAnalyzeCommandsProvider = () => CommandRegistry.FullAnalyzeCommands;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    HelpPrinter.Print(CommandRegistry.All);
    return 0;
}

bool showMemory = args.Contains("--debug");
var rawArgs     = args[1..].Where(static a => a != "--debug").ToArray();
bool isHelp     = rawArgs.Contains("--help") || rawArgs.Contains("-h");

var sw = Stopwatch.StartNew();
ToolMemoryDiagnostic.Start();

var cmd = CommandRegistry.Find(args[0]);
int result;
if (cmd is not null)
{
    result = cmd.Run(rawArgs);
}
else
{
    AnsiConsole.MarkupLine($"[red]Unknown command:[/] [bold]{Markup.Escape(args[0])}[/]");
    AnsiConsole.MarkupLine("[dim]Run[/] [bold]DumpDetective --help[/] [dim]to see available commands.[/]");
    result = 1;
}

sw.Stop();
if (!isHelp)
{
    if (showMemory)
        ToolMemoryDiagnostic.PrintSummary();
    else
        ToolMemoryDiagnostic.StopPoller();
    AnsiConsole.MarkupLine($"[dim]Total execution time:[/] [bold]{sw.Elapsed.TotalSeconds:F1}s[/]");
}

return result;
