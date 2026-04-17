using DumpDetective.Output;
using Spectre.Console;
using System.Diagnostics;

namespace DumpDetective.Core;

/// <summary>
/// Shared helpers used by every command: help display, common arg parsing,
/// and the standard execute-with-context wrapper.
/// </summary>
public static class CommandBase
{
    /// <summary>
    /// Validates the dump path, opens a <see cref="DumpContext"/>, creates the
    /// appropriate <see cref="IRenderSink"/>, and invokes <paramref name="body"/>.
    /// </summary>
    public static int Execute(
        string?  dumpPath,
        string?  outputPath,
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
            using var sink = IRenderSink.Create(outputPath);
            body(ctx, sink);
            if (sink.IsFile && sink.FilePath is not null)
                AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {Markup.Escape(sink.FilePath)}");
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
    /// Extracts the first positional (non-flag) argument as the dump path and the
    /// value of <c>--output / -o</c> from <paramref name="args"/>.
    /// </summary>
    public static (string? DumpPath, string? OutputPath) ParseCommon(string[] args)
    {
        string? dumpPath = null, outputPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--output" or "-o") && i + 1 < args.Length)
                outputPath = args[++i];
            else if (!args[i].StartsWith('-') && dumpPath is null)
                dumpPath = args[i];
        }
        return (dumpPath, outputPath);
    }

    /// <summary>
    /// Returns the effective <paramref name="requestedTop"/> value, automatically
    /// increasing it to at least 200 when writing to a file (so reports are more
    /// complete than the interactive console default).
    /// </summary>
    public static int EffectiveTop(int requestedTop, string? outputPath) =>
        outputPath is null ? requestedTop : Math.Max(requestedTop, 200);

    /// <summary>
    /// Runs a status spinner with <paramref name="message"/>, then prints the
    /// elapsed time once the body completes.
    /// </summary>
    public static void TimedStatus(string message, Action<StatusContext> body)
    {
        var sw = Stopwatch.StartNew();
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start(message, body);
        AnsiConsole.MarkupLine($"[dim]  ({sw.Elapsed.TotalSeconds:F1}s)[/]");
    }

    /// <summary>
    /// If <c>--help / -h</c> is present, renders the help panel and returns
    /// <see langword="true"/> (caller should return 0 immediately).
    /// </summary>
    public static bool TryHelp(string[] args, string helpText)
    {
        if (!args.Any(a => a is "--help" or "-h")) return false;
        var panel = new Panel(helpText)
        {
            Header = new PanelHeader("[bold] Help [/]")
        };
        panel.BorderColor(Color.Grey);
        AnsiConsole.Write(panel);
        return true;
    }

    /// <summary>
    /// When <see langword="true"/>, per-command verbose console prints
    /// (PrintAnalyzing, pass counters, Status spinners) are suppressed.
    /// <c>[ThreadStatic]</c> so each parallel worker thread has its own flag.
    /// </summary>
    [ThreadStatic] private static bool _suppressVerbose;
    public static bool SuppressVerbose { get => _suppressVerbose; set => _suppressVerbose = value; }

    /// <summary>
    /// Runs a spinner with <paramref name="message"/> and executes <paramref name="body"/>.
    /// When <see cref="SuppressVerbose"/> is <see langword="true"/> (parallel worker threads)
    /// the spinner is skipped and the body runs directly — preventing concurrent live renderable conflicts.
    /// </summary>
    public static void RunStatus(string message, Action body)
    {
        if (SuppressVerbose) { body(); return; }
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse("blue"))
            .Start(message, _ => body());
    }

    /// <summary>
    /// Overload that provides a status-update callback to the body.
    /// The callback updates the spinner text when live; it is a no-op when suppressed.
    /// </summary>
    public static void RunStatus(string message, Action<Action<string>> body)
    {
        if (SuppressVerbose) { body(_ => { }); return; }
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse("blue"))
            .Start(message, ctx => body(msg => ctx.Status(msg)));
    }

    /// <summary>Writes a styled "Analyzing: path" header line to the console.</summary>
    public static void PrintAnalyzing(string dumpPath)
    {
        if (SuppressVerbose) return;
        AnsiConsole.MarkupLine(
            $"[dim]Analyzing:[/] {Markup.Escape(Path.GetFileName(dumpPath))}  " +
            $"[dim]{Markup.Escape(Path.GetDirectoryName(dumpPath) ?? "")}[/]");
    }

    /// <summary>
    /// Standard output-file format hint to embed in command Help strings.
    /// All formats are supported by every command via <see cref="IRenderSink"/> —
    /// the <c>.json</c> output can be converted to any other format later with
    /// <c>DumpDetective render &lt;file.json&gt;</c>.
    /// </summary>
    public const string OutputFormats = ".html / .md / .txt / .json";
}
