using System.Diagnostics;
using System.Text;
using DumpDetective.Cli;
using DumpDetective.Reporting;
using Spectre.Console;

Console.OutputEncoding = Encoding.UTF8;

// Resolve version from entry assembly (e.g. "1.0.4823") and expose it to all sinks.
var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
DumpDetective.Core.Utilities.AppInfo.Version =
    ver is not null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}" : "dev";

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
    result = cmd.Run(WithDefaultHtmlOutput(args[0], rawArgs));
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

// ── Default HTML output injection ─────────────────────────────────────────────
// If the user didn't pass -o / --output, synthesise a default output path of
//   <dumpDir>/<commandName>_<dumpFileName>.html   (single-dump commands)
//   <directory>/<commandName>.html                (multi-dump / directory commands)
// and prepend it to rawArgs so every command picks it up via CliArgs.Parse
// without any individual command needing to know about the default.
static string[] WithDefaultHtmlOutput(string commandName, string[] rawArgs)
{
    // Already has an explicit output — leave args untouched
    if (rawArgs.Any(static a => a is "-o" or "--output"))
        return rawArgs;

    // ── Case 1: single dump file ─────────────────────────────────────────────
    string? dumpPath = rawArgs.FirstOrDefault(static a =>
        !a.StartsWith('-') &&
        (a.EndsWith(".dmp",  StringComparison.OrdinalIgnoreCase) ||
         a.EndsWith(".mdmp", StringComparison.OrdinalIgnoreCase)));

    // Fall back to DD_DUMP env var when no positional dump was found
    dumpPath ??= Environment.GetEnvironmentVariable("DD_DUMP");

    if (dumpPath is not null)
    {
        string dir          = Path.GetDirectoryName(dumpPath) ?? ".";
        string file         = Path.GetFileName(dumpPath);
        string defaultOutput = Path.Combine(dir, $"{commandName}_{file}.html");
        return [..rawArgs, "--output", defaultOutput];
    }

    // ── Case 2: directory argument (e.g. trend-analysis, trend-render) ───────
    string? dirArg = rawArgs.FirstOrDefault(a =>
        !a.StartsWith('-') && Directory.Exists(a));

    if (dirArg is not null)
    {
        string defaultOutput = Path.Combine(dirArg, $"{commandName}.html");
        return [..rawArgs, "--output", defaultOutput];
    }

    return rawArgs; // no positional found → let the command handle missing input
}
