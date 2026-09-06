namespace DumpDetective.Commands.Web;

/// <summary>
/// Analyzes a Chrome DevTools performance trace (.json / .json.gz) for JS heap growth,
/// DOM node accumulation, and event-listener leaks — the browser-side counterpart to
/// <c>memory-leak</c> for .NET dumps.
/// </summary>
public sealed class WebMemoryLeakCommand : ICommand, IWebSubAnalyzer
{
    public string Name                  => "web-memory-leak";
    public string Description           => "Memory/listener leak trend from a Chrome DevTools performance trace (.json / .json.gz).";
    public bool   IncludeInFullAnalyze  => false;
    public string Category              => "Web Performance";
    public CommandKind Kind             => CommandKind.Web;
    public string Key                   => Name;
    public string SectionTitle          => "Memory / Listener Leak";

    public IReadOnlyList<Finding> Run(
        WebTraceData trace, string traceFileName,
        Dictionary<string, ReportDoc> captured, Dictionary<string, object?> results)
    {
        var sink = new CaptureSink();
        sink.Header(SectionTitle, traceFileName, navLevel: 3, commandName: Name);
        var data = WebMemoryLeakAnalyzer.Analyze(trace, traceFileName);
        new WebMemoryLeakReport().Render(data, sink);
        captured[Name] = sink.GetDoc();
        results[Name]  = data;
        return data.Findings;
    }

    private const string Help = """
        Usage: DumpDetective web-memory-leak <trace.json.gz> [options]

        Parses UpdateCounters samples from a Chrome DevTools Performance recording to report:
          • JS heap / DOM node / event-listener trend over the recording
          • Listener : node ratio — the strongest single signal of a listener leak
          • Net growth from the first tenth of the recording to the last tenth

        Collecting a trace:
          Chrome DevTools → Performance panel → check "Memory" → Record → stop → Export
          (gzip the exported .json, or point this command at the plain .json)

        Options:
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help

        Examples:
          DumpDetective web-memory-leak trace.json.gz
          DumpDetective web-memory-leak trace.json.gz --output report.html
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var a = CliArgs.Parse(args);
        string? tracePath = a.DumpPath ?? a.Positionals.FirstOrDefault();
        if (!ValidateWebTrace(ref tracePath, Help)) return 1;

        var outputPaths = a.EffectiveOutputPaths.Count > 0
            ? a.EffectiveOutputPaths
            : (IReadOnlyList<string>)[CommandBase.DefaultOutputPath(tracePath!, ".html")];
        using var sink = SinkFactory.CreateMulti(outputPaths);
        try
        {
            if (!CommandBase.SuppressVerbose)
                AnsiConsole.MarkupLine($"[bold]Analyzing:[/] {Markup.Escape(Path.GetFileName(tracePath!))}");

            WebMemoryLeakData? data = null;
            CommandBase.RunStatus(Name, update =>
            {
                var trace = WebTraceContext.Open(tracePath!, s => update(s));
                data = WebMemoryLeakAnalyzer.Analyze(trace, Path.GetFileName(tracePath!));
            });

            sink.Header($"Dump Detective — Web Memory / Listener Leak", Path.GetFileName(tracePath!));
            new WebMemoryLeakReport().Render(data!, sink);

            foreach (var p in outputPaths)
                AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {ProgressLogger.FileLink(p)}");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]✗ Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    public void Render(DumpContext ctx, IRenderSink sink) =>
        sink.Alert(AlertLevel.Warning,
            "web-memory-leak requires a Chrome DevTools trace (.json / .json.gz) — it cannot analyze a memory dump.");

    internal static bool ValidateWebTrace(ref string? tracePath, string help)
    {
        if (tracePath is null)
        {
            AnsiConsole.MarkupLine("[bold red]✗[/] Trace file path required (.json or .json.gz).");
            AnsiConsole.MarkupLine(Markup.Escape(help));
            return false;
        }
        if (!File.Exists(tracePath))
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] File not found: {Markup.Escape(tracePath)}");
            return false;
        }
        if (!CliArgs.IsWebTraceFile(tracePath))
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] Unsupported file type. Expected .json or .json.gz — got: {Markup.Escape(Path.GetFileName(tracePath))}");
            return false;
        }
        return true;
    }
}
