using DumpDetective.Tests.Fixtures;
using DumpDetective.DiagnosticScenarios;
using DumpDetective.DiagnosticScenarios.Scenarios.Async;

namespace DumpDetective.Tests.Integration;

public sealed class AsyncStacksTest(CommandContext<AsyncStacksScenario> ctx)
    : ScenarioTestBase<AsyncStacksScenario>(ctx), IClassFixture<CommandContext<AsyncStacksScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
