namespace DumpDetective.Commands.Web;

/// <summary>
/// Runs every registered web sub-analyzer against one Chrome DevTools trace and produces
/// a single report: health score, ranked Action Queue (Now/Next/Watch), a "Look here
/// first" pointer, then each sub-analyzer's full section — the web-trace counterpart to
/// <c>analyze --full</c> / <c>trace-analyze</c>.
/// </summary>
public sealed class WebAnalyzeCommand : ICommand
{
    private readonly IReadOnlyList<IWebSubAnalyzer> _subAnalyzers;
    private readonly IReadOnlyList<IWebSubAnalyzer> _pluginSubAnalyzers;

    public WebAnalyzeCommand(
        IReadOnlyList<IWebSubAnalyzer> subAnalyzers,
        IReadOnlyList<IWebSubAnalyzer>? pluginSubAnalyzers = null)
    {
        _subAnalyzers       = subAnalyzers;
        _pluginSubAnalyzers = pluginSubAnalyzers ?? [];
    }

    public string Name                 => "web-analyze";
    public string Description          => "Full web-performance analysis (memory/listener leak, CPU hotspots, long tasks, GC pressure, network, jank, input latency) with a ranked Action Queue. Supports --fail-on for CI gating.";
    public bool   IncludeInFullAnalyze => false;
    public string Category             => "Web Performance";
    public CommandKind Kind            => CommandKind.Web;

    private const string Help = """
        Usage: DumpDetective web-analyze <trace.json.gz> [options]

        Runs every built-in web-trace analyzer (memory/listener leak, CPU hotspots, long
        tasks, GC pressure) against one recording and produces a single report: a 0-100
        health score, a ranked Action Queue (Now/Next/Watch), a "Look here first" pointer
        to the highest-priority file/finding, then every sub-analyzer's full section.

        Options:
          --with-plugins         Also run plugin-contributed web analyzers
          --fail-on <level>      Exit with code 2 if any finding meets this severity —
                                  "critical" or "warning" (warning also catches critical).
                                  For CI: fail the build on a performance regression instead
                                  of only surfacing it in a report someone has to open.
          -o, --output <file>    Write report to file (.html / .md / .txt / .json)
          -h, --help             Show this help

        Examples:
          DumpDetective web-analyze trace.json.gz
          DumpDetective web-analyze trace.json.gz --output report.html
          DumpDetective web-analyze trace.json.gz --fail-on critical
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var a = CliArgs.Parse(args);
        string? tracePath = a.DumpPath ?? a.Positionals.FirstOrDefault();
        if (!WebMemoryLeakCommand.ValidateWebTrace(ref tracePath, Help)) return 1;

        bool withPlugins = a.HasFlag("with-plugins");
        IReadOnlyList<IWebSubAnalyzer> analyzers = withPlugins
            ? [.._subAnalyzers, .._pluginSubAnalyzers]
            : _subAnalyzers;

        var outputPaths = a.EffectiveOutputPaths.Count > 0
            ? a.EffectiveOutputPaths
            : (IReadOnlyList<string>)[CommandBase.DefaultOutputPath(tracePath!, ".html")];
        using var sink = SinkFactory.CreateMulti(outputPaths);
        try
        {
            if (!CommandBase.SuppressVerbose)
                AnsiConsole.MarkupLine($"[bold]Analyzing:[/] {Markup.Escape(Path.GetFileName(tracePath!))}");

            WebTraceData? trace = null;
            CommandBase.RunStatus("Opening trace...", update => trace = WebTraceContext.Open(tracePath!, s => update(s)));

            string fileName = Path.GetFileName(tracePath!);
            var captured     = new Dictionary<string, ReportDoc>();
            var results      = new Dictionary<string, object?>();
            var allFindings  = new List<Finding>();

            foreach (var analyzer in analyzers)
            {
                CommandBase.RunStatus(analyzer.Key, _ =>
                {
                    var findings = analyzer.Run(trace!, fileName, captured, results);
                    allFindings.AddRange(findings.Where(f => f.Category != "Summary"));
                });
            }

            // Unlike HealthScorer's dump-side findings (already emitted in a meaningful
            // order by one analyzer), these come from several independent analyzers with
            // no cross-analyzer ordering — sort by severity then deduction so the Action
            // Queue's P1/P2/P3 reflect actual urgency, not just "whichever analyzer ran first".
            allFindings.Sort(static (a, b) =>
            {
                int c = b.Severity.CompareTo(a.Severity);
                return c != 0 ? c : b.Deduction.CompareTo(a.Deduction);
            });

            sink.Header("Dump Detective — Web Performance Analysis", fileName);
            new WebAnalyzeReport().Render(
                fileName, allFindings, captured,
                analyzers.Select(x => (x.Key, x.SectionTitle)).ToList(),
                sink,
                trace!.Screenshots.Select(s => (s.TimestampUs / 1000, s.Base64Jpeg)).ToList());

            foreach (var p in outputPaths)
                AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {ProgressLogger.FileLink(p)}");

            string? failOn = a.GetOption("fail-on");
            if (failOn is not null && GateTriggered(allFindings, failOn, out string? gateReason))
            {
                AnsiConsole.MarkupLine($"\n[bold red]✗ --fail-on {Markup.Escape(failOn)}:[/] {Markup.Escape(gateReason!)}");
                return 2;
            }
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]✗ Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    /// <summary>
    /// CI gate: "critical" fails only on a Critical finding; "warning" fails on either.
    /// Unrecognised levels never trigger the gate (fail open, not closed, on a typo).
    /// </summary>
    private static bool GateTriggered(IReadOnlyList<Finding> findings, string failOn, out string? reason)
    {
        bool wantCritical = failOn.Equals("critical", StringComparison.OrdinalIgnoreCase);
        bool wantWarning  = failOn.Equals("warning",  StringComparison.OrdinalIgnoreCase);
        if (!wantCritical && !wantWarning) { reason = null; return false; }

        var worst = findings
            .Where(f => wantWarning
                ? f.Severity is FindingSeverity.Warning or FindingSeverity.Critical
                : f.Severity is FindingSeverity.Critical)
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.Deduction)
            .FirstOrDefault();

        if (worst is null) { reason = null; return false; }
        reason = $"{worst.Severity} finding present — {worst.Headline}";
        return true;
    }

    public void Render(DumpContext ctx, IRenderSink sink) =>
        sink.Alert(AlertLevel.Warning,
            "web-analyze requires a Chrome DevTools trace (.json / .json.gz) — it cannot analyze a memory dump.");
}
