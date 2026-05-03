using DumpDetective.Tests.Fixtures;
using DumpDetective.DiagnosticScenarios;
using DumpDetective.DiagnosticScenarios.Scenarios.Types;

namespace DumpDetective.Tests.Integration;

public sealed class TypeInstancesTest(CommandContext<TypeInstancesScenario> ctx)
    : ScenarioTestBase<TypeInstancesScenario>(ctx), IClassFixture<CommandContext<TypeInstancesScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
