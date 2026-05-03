using DumpDetective.Tests.Fixtures;
using DumpDetective.Tests.Scenarios.Leaks;

namespace DumpDetective.Tests.Integration;

public sealed class ConnectionPoolTest(CommandContext<ConnectionPoolScenario> ctx)
    : ScenarioTestBase<ConnectionPoolScenario>(ctx), IClassFixture<CommandContext<ConnectionPoolScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
