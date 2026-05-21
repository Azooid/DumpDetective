using DumpDetective.Core.Interfaces;

namespace Example.DumpDetective;

public sealed class Manifest : IPluginManifest
{
    public string  PluginName => "Example.DumpDetective";
    public string? Version    => "1.0.0";

    public IEnumerable<ICommand> RegisterCommands()
    {
        // Memory dump commands
        yield return new DuplicateModulesCommand();
        yield return new NamespaceHeapCommand();
        yield return new ThreadHotspotsCommand();

        // Trace commands (standalone ICommand only — no ITraceSubAnalyzer)
        yield return new TraceEventInventoryCommand();

        // Trace + dump correlation rules (ITraceDumpCorrelationRule).
        // These also implement ICommand (required for discovery). They have no
        // standalone output — Run() returns 0 immediately. They are evaluated
        // automatically by trace-dump-analyze --with-plugins after all built-in rules.
        yield return new SqlTimeoutVsConnectionCountRule();
        yield return new ContentionBurstVsBlockedThreadsRule();
        yield return new LohGrowthConfirmedInDumpRule();
        yield return new HttpP99LatencyVsAsyncDensityRule();
    }
}
