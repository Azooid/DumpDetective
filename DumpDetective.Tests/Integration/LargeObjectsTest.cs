using DumpDetective.Tests.Fixtures;
using DumpDetective.DiagnosticScenarios;
using DumpDetective.DiagnosticScenarios.Scenarios.Heap;

namespace DumpDetective.Tests.Integration;

public sealed class LargeObjectsTest(CommandContext<LargeObjectsScenario> ctx)
    : ScenarioTestBase<LargeObjectsScenario>(ctx), IClassFixture<CommandContext<LargeObjectsScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
