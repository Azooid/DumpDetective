namespace DumpDetective.Commands.Web;

/// <summary>Summarizes V8 GC pause activity (major/minor cycles + pause durations) from a Chrome DevTools or Firefox Profiler performance trace.</summary>
public sealed class WebGcPressureCommand : ICommand, IWebSubAnalyzer
{
    public string Name                 => "web-gc-pressure";
    public string Description          => "V8 GC pause summary from a Chrome DevTools or Firefox Profiler performance trace (.json / .json.gz).";
    public bool   IncludeInFullAnalyze => false;
    public string Category             => "Web Performance";
    public CommandKind Kind            => CommandKind.Web;
    public string Key                  => Name;
    public string SectionTitle         => "GC Pressure";

    public IReadOnlyList<Finding> Run(
        WebTraceData trace, string traceFileName,
        Dictionary<string, ReportDoc> captured, Dictionary<string, object?> results)
    {
        var sink = new CaptureSink();
        sink.Header(SectionTitle, traceFileName, navLevel: 3, commandName: Name);
        var data = WebGcPressureAnalyzer.Analyze(trace, traceFileName);
        new WebGcPressureReport().Render(data, sink);
        captured[Name] = sink.GetDoc();
        results[Name]  = data;
        return data.Findings;
    }

    private const string Help = """
        Usage: DumpDetective web-gc-pressure <trace.json.gz> [options]

        Summarizes V8 major (full-heap) and minor (young-gen) GC cycles captured during
        the recording — cycle counts and pause durations.

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

            WebGcPressureData? data = null;
            CommandBase.RunStatus(Name, update =>
            {
                var trace = WebTraceContext.Open(tracePath!, s => update(s));
                data = WebGcPressureAnalyzer.Analyze(trace, Path.GetFileName(tracePath!));
            });

            sink.Header("Dump Detective — Web GC Pressure", Path.GetFileName(tracePath!));
            new WebGcPressureReport().Render(data!, sink);

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
            "web-gc-pressure requires a browser performance trace (Chrome DevTools or Firefox Profiler; .json / .json.gz) — it cannot analyze a memory dump.");
}
