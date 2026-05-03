using DumpDetective.Tests.Fixtures;
using DumpDetective.DiagnosticScenarios;
using DumpDetective.DiagnosticScenarios.Scenarios.Types;

namespace DumpDetective.Tests.Integration;

public sealed class ModuleListTest(CommandContext<ModuleListScenario> ctx)
    : ScenarioTestBase<ModuleListScenario>(ctx), IClassFixture<CommandContext<ModuleListScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
