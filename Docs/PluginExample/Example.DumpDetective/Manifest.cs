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
    }
}
