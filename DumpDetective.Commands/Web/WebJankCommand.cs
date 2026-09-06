namespace DumpDetective.Commands.Web;

/// <summary>Compositor frame-drop rate (jank) from a Chrome DevTools performance trace.</summary>
public sealed class WebJankCommand : ICommand, IWebSubAnalyzer
{
    public string Name                 => "web-jank";
    public string Description          => "Dropped-frame / jank rate from a Chrome DevTools performance trace (.json / .json.gz).";
    public bool   IncludeInFullAnalyze => false;
    public string Category             => "Web Performance";
    public CommandKind Kind            => CommandKind.Web;
    public string Key                  => Name;
    public string SectionTitle         => "Jank / Dropped Frames";

    public IReadOnlyList<Finding> Run(
        WebTraceData trace, string traceFileName,
        Dictionary<string, ReportDoc> captured, Dictionary<string, object?> results)
    {
        var sink = new CaptureSink();
        sink.Header(SectionTitle, traceFileName, navLevel: 3, commandName: Name);
        var data = WebJankAnalyzer.Analyze(trace, traceFileName);
        new WebJankReport().Render(data, sink);
        captured[Name] = sink.GetDoc();
        results[Name]  = data;
        return data.Findings;
    }

    private const string Help = """
        Usage: DumpDetective web-jank <trace.json.gz> [options]

        Computes the compositor frame-drop rate (BeginFrame vs. DroppedFrame events) — the
        stutter/jank a user would actually perceive, independent of raw CPU cost.

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

            WebJankData? data = null;
            CommandBase.RunStatus(Name, update =>
            {
                var trace = WebTraceContext.Open(tracePath!, s => update(s));
                data = WebJankAnalyzer.Analyze(trace, Path.GetFileName(tracePath!));
            });

            sink.Header("Dump Detective — Web Jank", Path.GetFileName(tracePath!));
            new WebJankReport().Render(data!, sink);

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
            "web-jank requires a Chrome DevTools trace (.json / .json.gz) — it cannot analyze a memory dump.");
}
