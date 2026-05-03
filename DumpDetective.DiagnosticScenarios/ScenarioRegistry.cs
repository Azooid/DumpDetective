namespace DumpDetective.DiagnosticScenarios;

internal sealed class ScenarioEntry
{
    public Action Setup    { get; init; } = static () => { };
    public Action Teardown { get; init; } = static () => { };
}

internal static class ScenarioRegistry
{
    public static readonly Dictionary<string, ScenarioEntry> All = Build();

    private static Dictionary<string, ScenarioEntry> Build()
    {
        var d = new Dictionary<string, ScenarioEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in ScenarioList.All)
            d[s.CommandName] = new ScenarioEntry { Setup = s.Setup, Teardown = s.Teardown };
        return d;
    }
}
