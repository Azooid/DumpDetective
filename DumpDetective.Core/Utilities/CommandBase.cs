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

    // ── Per-command operation trace ───────────────────────────────────────────
    // [ThreadStatic] so parallel full-analyze workers each have their own trace.

    [ThreadStatic] private static List<(string Label, long Ms)>? _trace;

    /// <summary>
    /// Activates operation tracing on this thread. Call immediately before
    /// <c>BuildReport</c>. Every subsequent <see cref="RunStatus"/> call will
    /// record its label and elapsed milliseconds into the trace.
    /// </summary>
    public static void BeginTrace() => _trace = new List<(string, long)>(8);

    /// <summary>
    /// Ends tracing, formats the recorded entries into sub-lines (2 per line),
    /// and clears the trace list. Returns <see langword="null"/> when nothing
    /// was traced (command used only cached snapshot data).
    /// </summary>
    public static string[]? EndTrace()
    {
        var t = _trace;
        _trace = null;
        if (t is null || t.Count == 0) return null;

        var formatted = new List<string>(t.Count);
        foreach (var (label, ms) in t)
        {
            string elapsed = ms < 1000 ? $"{ms}ms" : $"{ms / 1000.0:F1}s";
            // Strip trailing "..." from messages like "Scanning GC handles..."
            string clean = label.EndsWith("...") ? label[..^3].TrimEnd() : label;
            formatted.Add($"{clean} • {elapsed}");
        }

        // Pack 2 entries per sub-line for compact display
        var lines = new List<string>();
        for (int i = 0; i < formatted.Count; i += 2)
        {
            lines.Add(i + 1 < formatted.Count
                ? $"{formatted[i]}  |  {formatted[i + 1]}"
                : formatted[i]);
        }
        return lines.ToArray();
    }

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
                AnsiConsole.MarkupLine("[dim]ℹ No output file — printing to console. Use -o / --output <file> to save as .html / .md / .txt / .json[/]\n");

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
    /// Always records label + elapsed time into the active trace if one is running.
    /// </summary>
    public static void RunStatus(string message, Action body)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (SuppressVerbose) body();
            else AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse("blue"))
                .Start(message, _ => body());
        }
        finally
        {
            sw.Stop();
            _trace?.Add((message, sw.ElapsedMilliseconds));
        }
    }

    /// <summary>Overload that provides a status-update callback to the body.</summary>
    public static void RunStatus(string message, Action<Action<string>> body)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (SuppressVerbose) body(_ => { });
            else AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse("blue"))
                .Start(message, ctx => body(msg => ctx.Status(msg)));
        }
        finally
        {
            sw.Stop();
            _trace?.Add((message, sw.ElapsedMilliseconds));
        }
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
