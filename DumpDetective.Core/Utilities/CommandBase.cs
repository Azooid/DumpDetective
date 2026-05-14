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
    // ── SuppressVerbose — forwarded to ExecutionContext ───────────────────────
    public static bool SuppressVerbose
    {
        get => ExecutionContext.SuppressVerbose;
        set => ExecutionContext.SuppressVerbose = value;
    }

    // ── PersistCache — forwarded to ExecutionContext ──────────────────────────


    // ── Parameter overrides — forwarded to ExecutionContext ───────────────────
    public static void SetOverride(string key, string value)       => ExecutionContext.SetOverride(key, value);
    public static void SetSharedOverride(string key, string value) => ExecutionContext.SetSharedOverride(key, value);
    public static void ClearOverrides()                            => ExecutionContext.ClearOverrides();
    public static string? GetOverride(string key)                  => ExecutionContext.GetOverride(key);
    public static int  GetOverrideInt (string key, int  @default)  => ExecutionContext.GetOverrideInt(key, @default);
    public static long GetOverrideLong(string key, long @default)  => ExecutionContext.GetOverrideLong(key, @default);

    // ── Operation trace — forwarded to OperationTrace ─────────────────────────
    public static void     BeginTrace() => OperationTrace.BeginTrace();
    public static string[]? EndTrace()  => OperationTrace.EndTrace();

    public const string OutputFormats = ".html / .md / .txt / .json";

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
        => Execute(dumpPath, outputPath is not null ? new[] { outputPath } : null, body);

    /// <summary>
    /// <see cref="CliArgs"/>-aware overload. Convenience wrapper that extracts
    /// dump path and output paths from <paramref name="a"/>.
    /// </summary>
    public static int Execute(
        CliArgs a,
        Action<DumpContext, IRenderSink> body)
    {
        return Execute(a.DumpPath, a.EffectiveOutputPaths, body);
    }

    /// <summary>
    /// Multi-output overload. Each path in <paramref name="outputPaths"/> receives its own sink,
    /// fanned out via <see cref="TeeRenderSink"/>. When the list is empty or null,
    /// defaults to an HTML file alongside the dump.
    /// </summary>
    public static int Execute(
        string? dumpPath,
        IReadOnlyList<string>? outputPaths,
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

        // Build effective path list early so we can print it before opening the dump.
        string[] effectivePaths = (outputPaths is { Count: > 0 })
            ? outputPaths.ToArray()
            : [DefaultOutputPath(dumpPath, ".html")];

        foreach (var p in effectivePaths)
            AnsiConsole.MarkupLine($"[dim][[{Now}]] → Output:[/] {Markup.Escape(Path.GetFullPath(p))}");

        try
        {
            using var ctx = DumpContext.Open(dumpPath);
            if (ctx.ArchWarning is not null)
                AnsiConsole.MarkupLine($"[yellow]⚠ {Markup.Escape(ctx.ArchWarning)}[/]");

            using var sink = SinkFactory.CreateMulti(effectivePaths);
            body(ctx, sink);

            foreach (var p in effectivePaths)
                AnsiConsole.MarkupLine($"\n[dim][[{Now}]][/] [green]✓[/] Written to: {ProgressLogger.FileLink(p)}");
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
    /// Builds a default output path from any input file path, replacing spaces in the
    /// filename with underscores so the result is shell-friendly.
    /// Used by memory commands (via Execute) and trace commands for their HTML default.
    /// </summary>
    public static string DefaultOutputPath(string dumpPath, string extension)
    {
        var dir      = Path.GetDirectoryName(dumpPath) ?? ".";
        var filename = Path.GetFileNameWithoutExtension(dumpPath).Replace(' ', '_');
        return Path.Combine(dir, filename + extension);
    }

    /// <summary>
    /// Checks whether the heap can be walked. If not, writes a warning alert to
    /// <paramref name="sink"/> and returns <see langword="false"/> so the caller
    /// can return immediately. Returns <see langword="true"/> when the heap is walkable.
    /// </summary>
    public static bool EnsureCanWalkHeap(Microsoft.Diagnostics.Runtime.ClrHeap heap, IRenderSink sink)
    {
        if (heap.CanWalkHeap) return true;
        sink.Alert(AlertLevel.Warning, "Cannot walk heap.",
            "The dump may be incomplete or was captured without a full heap snapshot.");
        return false;
    }

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

    // ── Timestamp helper ────────────────────────────────────────────────────
    private static string Now => DateTime.Now.ToString("HH:mm:ss");

    private static string FormatElapsed(long ms) =>
        ms < 1000 ? $"{ms}ms" : $"{ms / 1000.0:F1}s";

    // Strip trailing "..." from spinner messages before printing as a done line.
    private static string CleanLabel(string msg) =>
        msg.EndsWith("...", StringComparison.Ordinal) ? msg[..^3].TrimEnd() : msg;

    private static void PrintDone(string message, long ms, string? suffix = null)
    {
        // Only print the permanent ✓ line when not suppressed (not in parallel mode)
        if (SuppressVerbose) return;
        string elapsed = FormatElapsed(ms);
        string label   = CleanLabel(message);
        string sfx     = suffix is not null ? $":  {Markup.Escape(suffix)}" : "";
        AnsiConsole.MarkupLine($"[dim][[{Now}]][/] [green]✓[/] {Markup.Escape(label)}{sfx}  [dim]({elapsed})[/]");
    }

    /// <summary>
    /// Runs a spinner with <paramref name="message"/> and executes <paramref name="body"/>.
    /// Prints a permanent timestamped ✓ line when done.
    /// No-op spinner (body runs directly) when <see cref="SuppressVerbose"/> is true.
    /// Always records label + elapsed time into the active trace if one is running.
    /// </summary>
    public static void RunStatus(string message, Action body)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (SuppressVerbose) body();
            else AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse("blue"))
                .Start(Markup.Escape(message), _ => body());
        }
        finally
        {
            sw.Stop();
            OperationTrace.Add(message, sw.ElapsedMilliseconds);
            PrintDone(message, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>Overload that provides a status-update callback to the body.</summary>
    public static void RunStatus(string message, Action<Action<string>> body)
    {
        var sw = Stopwatch.StartNew();
        string? scanSuffix = null; // populated when a [SCAN] token is received
        try
        {
            if (SuppressVerbose) body(_ => { });
            else AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse("blue"))
                .Start(Markup.Escape(message), ctx => body(msg =>
                {
                    if (msg.StartsWith("[SCAN]", StringComparison.Ordinal))
                    {
                        // Capture the heap-walk stats for the done line; don't print separately.
                        scanSuffix = FormatScanSuffix(msg[6..]);
                        return;
                    }
                    ctx.Status(Markup.Escape($"{msg}  ({sw.Elapsed.TotalSeconds:F1}s)"));
                }));
        }
        finally
        {
            sw.Stop();
            OperationTrace.Add(message, sw.ElapsedMilliseconds);
            PrintDone(message, sw.ElapsedMilliseconds, scanSuffix);
        }
    }

    /// <summary>
    /// Parses <c>label|count|ms</c> from a [SCAN] inner string and returns a compact
    /// stats suffix like <c>7,132,101 objs  •  ~1,971,827/s</c>, or <see langword="null"/>.
    /// </summary>
    private static string? FormatScanSuffix(string inner)
    {
        int p1 = inner.IndexOf('|');
        int p2 = p1 >= 0 ? inner.IndexOf('|', p1 + 1) : -1;
        if (p1 > 0 && p2 > p1
            && long.TryParse(inner[(p1 + 1)..p2], out long count)
            && long.TryParse(inner[(p2 + 1)..],   out long ms))
        {
            double secs = ms / 1000.0;
            long   rate = secs > 0 ? (long)(count / secs) : 0;
            return $"{count:N0} objs  •  ~{rate:N0}/s";
        }
        return null;
    }

    /// <summary>
    /// Wraps a <see cref="RunStatus"/> update callback to filter out <c>[SCAN]</c>
    /// ProgressLogger protocol messages that are not meaningful for spinner display.
    /// Use this when passing the update callback straight into <c>HeapWalker.Walk</c>.
    /// </summary>
    public static Action<string> StatusProgress(Action<string> update) =>
        msg =>
        {
            if (!msg.StartsWith("[SCAN]", StringComparison.Ordinal))
                update(msg);
        };

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
    /// Emits a ProgressLogger-style section banner and info line, then writes the
    /// report header to <paramref name="sink"/>.
    /// Call this as the first line of every command's Render method.
    /// </summary>
    public static void RenderHeader(string title, DumpContext ctx, IRenderSink sink)
    {
        if (!SuppressVerbose)
        {
            // Section banner ── Title ─────────────────────────────────────────
            int w;
            try   { w = Console.WindowWidth > 20 ? Console.WindowWidth - 1 : 100; }
            catch { w = 100; }
            int dashes = Math.Max(4, w - title.Length - 4);
            AnsiConsole.MarkupLine($"[bold]── {Markup.Escape(title)} {new string('─', dashes)}[/]");

            // Timestamped info line: file | directory | CLR version
            AnsiConsole.MarkupLine(
                $"[dim][[{Now}]][/] [blue]ℹ[/] " +
                $"{Markup.Escape(Path.GetFileName(ctx.DumpPath))}  " +
                $"[dim]{Markup.Escape(Path.GetDirectoryName(ctx.DumpPath) ?? "")}  |  " +
                $"CLR {Markup.Escape(ctx.ClrVersion ?? "unknown")}[/]");
        }
        sink.Header($"Dump Detective — {title}", Subtitle(ctx));
    }
}
