using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Runtime;
using Spectre.Console;
using System.Diagnostics;

namespace DumpDetective.Core.Utilities;

/// <summary>
/// Shared infrastructure used by every command: execute lifecycle,
/// argument parsing helpers, spinner, help display.
/// </summary>
public static class CommandBase
{
    /// <summary>
    /// When <see langword="true"/> on the current thread, console spinners and
    /// progress output (PrintAnalyzing, RunStatus, TimedStatus) are suppressed.
    /// <c>[ThreadStatic]</c> so each parallel full-analyze worker is isolated.
    /// </summary>
    [ThreadStatic] private static bool _suppressVerbose;
    public static bool SuppressVerbose { get => _suppressVerbose; set => _suppressVerbose = value; }

    public const string OutputFormats = ".html / .md / .txt / .json";

    /// <summary>
    /// Injected by the Cli project at startup so that <c>AnalyzeCommand</c> (in Commands)
    /// can iterate all full-analyze commands without a circular project reference.
    /// </summary>
    public static Func<IEnumerable<ICommand>>? FullAnalyzeCommandsProvider { get; set; }

    /// <summary>
    /// Injected by <c>ReportingBootstrap.Register()</c> so the default <see cref="ICommand.BuildReport"/>
    /// implementation can build a <see cref="ReportDoc"/> without Core referencing Reporting.
    /// </summary>
    public static Func<ICommand, DumpContext, ReportDoc>? ReportDocBuilder { get; set; }

    /// <summary>
    /// Returns the standard dump-file subtitle string used in every report header:
    /// <c>&lt;filename&gt;  |  &lt;timestamp&gt;  |  CLR &lt;version&gt;</c>.
    /// </summary>
    public static string Subtitle(DumpContext ctx) =>
        $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}";

    /// <summary>
    /// Validates the dump path, opens a <see cref="DumpContext"/>, creates an
    /// <see cref="IRenderSink"/>, invokes <paramref name="body"/>, and returns
    /// the appropriate exit code.
    /// </summary>
    public static int Execute(
        string? dumpPath,
        string? outputPath,
        Action<DumpContext, IRenderSink> body)
    {
        if (dumpPath is null)
        {
            AnsiConsole.MarkupLine("[bold red]✗ Error:[/] dump file path is required.");
            return 1;
        }
        if (!File.Exists(dumpPath))
        {
            AnsiConsole.MarkupLine($"[bold red]✗ Error:[/] file not found: [dim]{Markup.Escape(dumpPath)}[/]");
            return 1;
        }

        try
        {
            using var ctx  = DumpContext.Open(dumpPath);
            if (ctx.ArchWarning is not null)
                AnsiConsole.MarkupLine($"[yellow]⚠ {Markup.Escape(ctx.ArchWarning)}[/]");

            using var sink = SinkFactory.Create(outputPath);

            bool consoleOnly = !sink.IsFile;
            if (consoleOnly)
                AnsiConsole.MarkupLine("[dim yellow]⚠ No output file specified — report is printed to console only and will not be saved.[/]\n");

            body(ctx, sink);

            if (sink.IsFile && sink.FilePath is not null)
                AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {Markup.Escape(sink.FilePath)}");
            else
                AnsiConsole.MarkupLine("\n[dim yellow]⚠ Report printed to console only — use -o / --output <file> to save as .html / .md / .txt / .json[/]");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLine($"[bold red]✗ Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]✗ Unexpected error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    /// <summary>
    /// Returns the effective <paramref name="top"/> value, automatically
    /// increasing it to at least 200 for file output.
    /// </summary>
    public static int EffectiveTop(int top, string? outputPath) =>
        outputPath is null ? top : Math.Max(top, 200);

    /// <summary>
    /// If <c>--help</c> / <c>-h</c> is present, renders the help panel and
    /// returns <see langword="true"/> (caller should return 0 immediately).
    /// </summary>
    public static bool TryHelp(string[] args, string helpText)
    {
        if (!args.Any(a => a is "--help" or "-h")) return false;
        var panel = new Panel(new Text(helpText)) { Header = new PanelHeader("[bold] Help [/]") };
        panel.BorderColor(Color.Grey);
        AnsiConsole.Write(panel);
        return true;
    }

    /// <summary>
    /// Runs a spinner with <paramref name="message"/> and executes <paramref name="body"/>.
    /// No-op (body runs directly) when <see cref="SuppressVerbose"/> is true.
    /// </summary>
    public static void RunStatus(string message, Action body)
    {
        if (SuppressVerbose) { body(); return; }
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse("blue"))
            .Start(message, _ => body());
    }

    /// <summary>Overload that provides a status-update callback to the body.</summary>
    public static void RunStatus(string message, Action<Action<string>> body)
    {
        if (SuppressVerbose) { body(_ => { }); return; }
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse("blue"))
            .Start(message, ctx => body(msg => ctx.Status(msg)));
    }

    public static void TimedStatus(string message, Action<StatusContext> body)
    {
        var sw = Stopwatch.StartNew();
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start(message, body);
        AnsiConsole.MarkupLine($"[dim]  ({sw.Elapsed.TotalSeconds:F1}s)[/]");
    }

    public static void PrintAnalyzing(string dumpPath)
    {
        if (SuppressVerbose) return;
        AnsiConsole.MarkupLine(
            $"[dim]Analyzing:[/] {Markup.Escape(Path.GetFileName(dumpPath))}  " +
            $"[dim]{Markup.Escape(Path.GetDirectoryName(dumpPath) ?? "")}[/]");
    }

    /// <summary>
    /// Emits the standard "Analyzing: …" console line and writes the report header
    /// to <paramref name="sink"/>.  The full title is <c>"Dump Detective — {title}"</c>.
    /// Call this as the first line of every command's <c>RenderWith</c> method.
    /// </summary>
    public static void RenderHeader(string title, DumpContext ctx, IRenderSink sink)
    {
        PrintAnalyzing(ctx.DumpPath);
        sink.Header($"Dump Detective — {title}", Subtitle(ctx));
    }
}
