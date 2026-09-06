using DumpDetective.Commands.Web;
using DumpDetective.Core.Interfaces;

namespace DumpDetective.Cli;

/// <summary>
/// Single source of truth for all web-trace (Chrome DevTools performance trace)
/// commands — mirrors <see cref="TraceCommandRegistry"/>.
/// </summary>
public static class WebCommandRegistry
{
    private static readonly ICommand[] _standaloneCommands =
    [
        new WebMemoryLeakCommand(),
        new WebCpuHotspotCommand(),
        new WebLongTaskCommand(),
        new WebGcPressureCommand(),
        new WebNetworkCommand(),
        new WebJankCommand(),
        new WebInputLatencyCommand(),
    ];

    /// <summary>All standalone web commands, also usable as <see cref="IWebSubAnalyzer"/> instances.</summary>
    public static IReadOnlyList<ICommand> StandaloneCommands => _standaloneCommands;

    public static IReadOnlyList<IWebSubAnalyzer> SubAnalyzers { get; } =
        _standaloneCommands.OfType<IWebSubAnalyzer>().ToArray();

    /// <summary>Creates the <c>web-analyze</c> orchestrator. Pass a non-empty plugin list to enable <c>--with-plugins</c>.</summary>
    public static ICommand BuildOrchestrator(IReadOnlyList<IWebSubAnalyzer>? pluginSubAnalyzers = null) =>
        new WebAnalyzeCommand(SubAnalyzers, pluginSubAnalyzers ?? []);
}
