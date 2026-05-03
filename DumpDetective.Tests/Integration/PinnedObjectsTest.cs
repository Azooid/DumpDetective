using DumpDetective.Tests.Fixtures;
using DumpDetective.DiagnosticScenarios;
using DumpDetective.DiagnosticScenarios.Scenarios.Gc;

namespace DumpDetective.Tests.Integration;

public sealed class PinnedObjectsTest(CommandContext<PinnedObjectsScenario> ctx)
    : ScenarioTestBase<PinnedObjectsScenario>(ctx), IClassFixture<CommandContext<PinnedObjectsScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
