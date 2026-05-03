using DumpDetective.Tests.Fixtures;
using DumpDetective.Tests.Scenarios.Gc;

namespace DumpDetective.Tests.Integration;

public sealed class HandleTableTest(CommandContext<HandleTableScenario> ctx)
    : ScenarioTestBase<HandleTableScenario>(ctx), IClassFixture<CommandContext<HandleTableScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
