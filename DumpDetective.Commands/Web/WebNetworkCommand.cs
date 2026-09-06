namespace DumpDetective.Commands.Web;

/// <summary>
/// Ranks completed network requests by duration from a Chrome DevTools performance
/// trace. Accepts a Firefox Profiler trace too, but reports "no data" for one — this
/// parser doesn't currently map Firefox's network markers (see
/// <see cref="Analysis.WebTrace.Parsing.FirefoxProfileParser"/>).
/// </summary>
public sealed class WebNetworkCommand : ICommand, IWebSubAnalyzer
{
    public string Name                 => "web-network";
    public string Description          => "Network request waterfall (slow/failed requests) from a Chrome DevTools performance trace (.json / .json.gz).";
    public bool   IncludeInFullAnalyze => false;
    public string Category             => "Web Performance";
    public CommandKind Kind            => CommandKind.Web;
    public string Key                  => Name;
    public string SectionTitle         => "Network";

    public IReadOnlyList<Finding> Run(
        WebTraceData trace, string traceFileName,
        Dictionary<string, ReportDoc> captured, Dictionary<string, object?> results)
    {
        var sink = new CaptureSink();
        sink.Header(SectionTitle, traceFileName, navLevel: 3, commandName: Name);
        var data = WebNetworkAnalyzer.Analyze(trace, traceFileName);
        new WebNetworkReport().Render(data, sink);
        captured[Name] = sink.GetDoc();
        results[Name]  = data;
        return data.Findings;
    }

    private const string Help = """
        Usage: DumpDetective web-network <trace.json.gz> [options]

        Correlates ResourceSendRequest/ResourceReceiveResponse/ResourceFinish events by
        requestId and ranks completed requests by duration. Only requests captured within
        the recording window are shown — a long-running session recording started after
        page load may show few or none.

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

            WebNetworkData? data = null;
            CommandBase.RunStatus(Name, update =>
            {
                var trace = WebTraceContext.Open(tracePath!, s => update(s));
                data = WebNetworkAnalyzer.Analyze(trace, Path.GetFileName(tracePath!));
            });

            sink.Header("Dump Detective — Web Network", Path.GetFileName(tracePath!));
            new WebNetworkReport().Render(data!, sink);

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
            "web-network requires a browser performance trace (Chrome DevTools or Firefox Profiler; .json / .json.gz) — it cannot analyze a memory dump.");
}
