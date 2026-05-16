using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Reports;      // bring in any report helpers you need

namespace MyPlugin.DumpDetective;

// ── 1. Manifest ───────────────────────────────────────────────────────────────

/// <summary>
/// Plugin entry point.  The loader finds the single concrete class in the
/// assembly that implements <see cref="IPluginManifest"/> and calls
/// <see cref="RegisterCommands"/> once at startup.
/// </summary>
public sealed class Manifest : IPluginManifest
{
    public string  PluginName => "MyPlugin.DumpDetective";
    public string? Version    => "1.0.0";

    public IEnumerable<ICommand> RegisterCommands()
    {
        yield return new HelloWorldCommand();
        // yield return new AnotherCommand();
    }
}

// ── 2. Command ────────────────────────────────────────────────────────────────

public sealed class HelloWorldCommand : CommandBase, ICommand
{
    public string Name                => "hello-world";
    public string Description         => "Example plugin command — prints a greeting.";
    public bool   IncludeInFullAnalyze => false;
    public string Category             => "Plugins";
    public CommandKind Kind            => CommandKind.Memory;

    private const string Help = """
        Usage: DumpDetective hello-world <dump.dmp>

        An example plugin command.  Replace this with your own analysis logic.
        """;

    public int Run(string[] args)
    {
        if (TryHelp(args, Help)) return 0;
        return Execute(CliArgs.Parse(args), (ctx, sink) =>
        {
            sink.Header("Hello World", Subtitle(ctx));
            sink.Section("Greeting");
            sink.Alert(AlertLevel.Info, $"Dump loaded: {ctx.DumpPath}");
        });
    }

    public void Render(DumpContext ctx, IRenderSink sink)
        => ReportDocReplay.Replay(BuildReport(ctx), sink);

    public ReportDoc BuildReport(DumpContext ctx)
    {
        var cap = new CaptureSink();
        cap.Header("Hello World", Subtitle(ctx));
        cap.Section("Greeting");
        cap.Alert(AlertLevel.Info, $"Dump loaded: {ctx.DumpPath}");
        return cap.ToDoc();
    }
}
