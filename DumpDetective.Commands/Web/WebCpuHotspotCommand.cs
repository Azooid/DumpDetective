namespace DumpDetective.Commands.Web;

/// <summary>
/// Ranks main-thread CPU self-time from a Chrome DevTools or Firefox Profiler trace by
/// (source file, function, line) — the "which file do I look at" answer for a
/// slow/janky recording.
/// </summary>
public sealed class WebCpuHotspotCommand : ICommand, IWebSubAnalyzer
{
    public string Name                 => "web-cpu-hotspots";
    public string Description          => "Ranked CPU self-time by file/function from a Chrome DevTools or Firefox Profiler performance trace (.json / .json.gz).";
    public bool   IncludeInFullAnalyze => false;
    public string Category             => "Web Performance";
    public CommandKind Kind            => CommandKind.Web;
    public string Key                  => Name;
    public string SectionTitle         => "CPU Hotspots";

    public IReadOnlyList<Finding> Run(
        WebTraceData trace, string traceFileName,
        Dictionary<string, ReportDoc> captured, Dictionary<string, object?> results)
    {
        var sink = new CaptureSink();
        sink.Header(SectionTitle, traceFileName, navLevel: 3, commandName: Name);
        var data = WebCpuHotspotAnalyzer.Analyze(trace, traceFileName, 25);
        new WebCpuHotspotReport().Render(data, sink);
        captured[Name] = sink.GetDoc();
        results[Name]  = data;
        return data.Findings;
    }

    private const string Help = """
        Usage: DumpDetective web-cpu-hotspots <trace.json.gz> [options]

        Aggregates CPU profiler samples embedded in a Chrome DevTools Performance
        recording (V8) or a Firefox Profiler export (Gecko) by (source file, function,
        line) and ranks them by self-time — the single highest-value place to start
        reading is always the top row. Which recorder produced the file is detected
        automatically.

        Collecting a trace:
          Chrome  → DevTools → Performance panel → Record → stop → Export
          Firefox → profiler.firefox.com or Ctrl+Shift+E → Capture Recording → Save as file
          (gzip the exported .json, or point this command at the plain .json)

        Options:
          --top <N>            Top N call-frames to show (default: 25)
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help

        Examples:
          DumpDetective web-cpu-hotspots trace.json.gz
          DumpDetective web-cpu-hotspots trace.json.gz --top 50 --output report.html
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var a = CliArgs.Parse(args);
        int top = a.GetInt("top", 25);
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

            WebCpuHotspotData? data = null;
            CommandBase.RunStatus(Name, update =>
            {
                var trace = WebTraceContext.Open(tracePath!, s => update(s));
                data = WebCpuHotspotAnalyzer.Analyze(trace, Path.GetFileName(tracePath!), top);
            });

            sink.Header($"Dump Detective — Web CPU Hotspots", Path.GetFileName(tracePath!));
            new WebCpuHotspotReport().Render(data!, sink);

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
            "web-cpu-hotspots requires a browser performance trace (Chrome DevTools or Firefox Profiler; .json / .json.gz) — it cannot analyze a memory dump.");
}
