using DumpDetective.Tests.Fixtures;
using DumpDetective.Tests.Scenarios.Thread;

namespace DumpDetective.Tests.Integration;

public sealed class ThreadPoolTest(CommandContext<ThreadPoolScenario> ctx)
    : ScenarioTestBase<ThreadPoolScenario>(ctx), IClassFixture<CommandContext<ThreadPoolScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
