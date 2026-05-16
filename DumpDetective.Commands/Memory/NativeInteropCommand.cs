using DumpDetective.Analysis.Memory.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Reports;

namespace DumpDetective.Commands.Memory;

public sealed class NativeInteropCommand : ICommand
{
    private readonly NativeInteropAnalyzer _analyzer;
    private readonly NativeInteropReport   _report;

    public NativeInteropCommand(NativeInteropAnalyzer analyzer, NativeInteropReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "native-interop";
    public string Description        => "Detect threads executing in native/runtime interop frames.";
    public bool   IncludeInFullAnalyze => true;
    public string Category             => "Threads / Concurrency";

    private const string Help = """
        Usage: DumpDetective native-interop <dump-file> [options]

        Identifies threads currently blocked in native or runtime transition frames
        (P/Invoke calls, CLR helper stubs, GC coordination, interop transitions).
        Reports the top native call sites shared across threads and the full
        mixed managed+native stack for each affected thread.

        Options:
          -o, --output <file>  Write report (.html / .md / .txt / .json)
          -h, --help           Show this help
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;
        var a = CliArgs.Parse(args);
        return CommandBase.Execute(a, (ctx, sink) => Render(ctx, sink));
    }

    public void Render(DumpContext ctx, IRenderSink sink)
    {
        CommandBase.RenderHeader("Native Interop Analysis", ctx, sink);
        var data = _analyzer.Analyze(ctx);
        _report.Render(data, sink);
    }
}
