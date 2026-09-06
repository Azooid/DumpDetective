namespace DumpDetective.Commands.Web;

/// <summary>Interaction-to-response latency (INP-style) from a Chrome DevTools performance trace.</summary>
public sealed class WebInputLatencyCommand : ICommand, IWebSubAnalyzer
{
    public string Name                 => "web-input-latency";
    public string Description          => "Interaction responsiveness (INP-style) from a Chrome DevTools performance trace (.json / .json.gz).";
    public bool   IncludeInFullAnalyze => false;
    public string Category             => "Web Performance";
    public CommandKind Kind            => CommandKind.Web;
    public string Key                  => Name;
    public string SectionTitle         => "Input Latency";

    public IReadOnlyList<Finding> Run(
        WebTraceData trace, string traceFileName,
        Dictionary<string, ReportDoc> captured, Dictionary<string, object?> results)
    {
        var sink = new CaptureSink();
        sink.Header(SectionTitle, traceFileName, navLevel: 3, commandName: Name);
        var data = WebInputLatencyAnalyzer.Analyze(trace, traceFileName);
        new WebInputLatencyReport().Render(data, sink);
        captured[Name] = sink.GetDoc();
        results[Name]  = data;
        return data.Findings;
    }

    private const string Help = """
        Usage: DumpDetective web-input-latency <trace.json.gz> [options]

        Measures time from user interaction to browser response, directly from the trace's
        async InputLatency::* events — an Interaction-to-Next-Paint-style signal.

        Options:
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var a = CliArgs.Parse(args);
        string? tracePath = a.DumpPath ?? a.Positionals.FirstOrDefault();
        if (!WebMemoryLeakCommand.ValidateWebTrace(ref tracePath, Help)) return 1;

        var outputPaths = a.EffectiveOutputPaths.Count > 0
            ? a.EffectiveOutputPaths
            : (IReadOnlyList<string>)[CommandBase.DefaultOutputPath(tracePath!, ".html")];
        using var sink = SinkFactory.CreateMulti(outputPaths);
        try
        {
            if (!CommandBase.SuppressVerbose)
                AnsiConsole.MarkupLine($"[bold]Analyzing:[/] {Markup.Escape(Path.GetFileName(tracePath!))}");

            WebInputLatencyData? data = null;
            CommandBase.RunStatus(Name, update =>
            {
                var trace = WebTraceContext.Open(tracePath!, s => update(s));
                data = WebInputLatencyAnalyzer.Analyze(trace, Path.GetFileName(tracePath!));
            });

            sink.Header("Dump Detective — Web Input Latency", Path.GetFileName(tracePath!));
            new WebInputLatencyReport().Render(data!, sink);

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
            "web-input-latency requires a Chrome DevTools trace (.json / .json.gz) — it cannot analyze a memory dump.");
}
