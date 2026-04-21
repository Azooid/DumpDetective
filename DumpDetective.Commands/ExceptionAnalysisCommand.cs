namespace DumpDetective.Commands;

public sealed class ExceptionAnalysisCommand : ICommand
{
    private readonly ExceptionAnalysisAnalyzer _analyzer;
    private readonly ExceptionAnalysisReport   _report;

    public ExceptionAnalysisCommand(ExceptionAnalysisAnalyzer analyzer, ExceptionAnalysisReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "exception-analysis";
    public string Description        => "Analyze exception objects on the heap, correlate with active threads.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective exception-analysis <dump-file> [options]

        Options:
          -n, --top <N>      Top N exception types (default: 20)
          -f, --filter <t>   Only types whose name contains <t>
          -a, --addresses    Include object addresses
          -s, --stack        Show original throw stack per type
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        int     top       = a.GetInt("top",    20);
        string? filter    = a.Filter;
        bool    showAddr  = a.ShowAddresses;
        bool    showStack = a.HasFlag("stack")     || a.HasFlag("s");

        return CommandBase.Execute(a.DumpPath, a.OutputPath,
            (ctx, sink) => RenderWith(ctx, sink, top, filter, showAddr, showStack));
    }

    public void Render(DumpContext ctx, IRenderSink sink) => RenderWith(ctx, sink, 50, null, false, true);


    private void RenderWith(DumpContext ctx, IRenderSink sink,
        int top, string? filter, bool showAddr, bool showStack)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        // Build active-exception lookup from live threads (not stored in analyzer data)
        var activeByAddr = new Dictionary<ulong, (int ThreadId, uint OSThreadId, string? TypeName, string? Message, int HResult, string? InnerType, IReadOnlyList<string> ThreadFrames, IReadOnlyList<string> ThrowFrames)>();
        foreach (var t in ctx.Runtime.Threads)
        {
            var ex = t.CurrentException;
            if (ex is null) continue;

            // Live managed thread call stack (where the thread is RIGHT NOW)
            var threadFrames = t.EnumerateStackTrace(includeContext: false)
                .Select(f => f.ToString() ?? "")
                .Where(s => s.Length > 0)
                .ToList();

            // Original throw location stored inside the exception object
            var throwFrames = ex.StackTrace
                .Select(f => f.ToString() ?? "")
                .Where(s => s.Length > 0)
                .ToList();

            activeByAddr[ex.Address] = (
                t.ManagedThreadId, t.OSThreadId,
                ex.Type?.Name,
                ex.Message,
                ex.HResult,
                ex.Inner?.Type?.Name,
                (IReadOnlyList<string>)threadFrames,
                (IReadOnlyList<string>)throwFrames);
        }

        var data = _analyzer.Analyze(ctx);
        sink.Header("Dump Detective — Exception Analysis",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {data.TotalAll:N0} exception object(s)  |  {activeByAddr.Count} active");
        _report.Render(data, sink, activeByAddr, top, filter, showAddr, showStack);
    }
}
