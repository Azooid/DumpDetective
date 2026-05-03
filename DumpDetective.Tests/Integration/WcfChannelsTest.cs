using DumpDetective.Tests.Fixtures;
using DumpDetective.Tests.Scenarios.Leaks;

namespace DumpDetective.Tests.Integration;

public sealed class WcfChannelsTest(CommandContext<WcfChannelsScenario> ctx)
    : ScenarioTestBase<WcfChannelsScenario>(ctx), IClassFixture<CommandContext<WcfChannelsScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
