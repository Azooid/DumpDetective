using DumpDetective.Tests.Fixtures;
using DumpDetective.DiagnosticScenarios;
using DumpDetective.DiagnosticScenarios.Scenarios.Heap;

namespace DumpDetective.Tests.Integration;

public sealed class StringDuplicatesTest(CommandContext<StringDuplicatesScenario> ctx)
    : ScenarioTestBase<StringDuplicatesScenario>(ctx), IClassFixture<CommandContext<StringDuplicatesScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
