using DumpDetective.Tests.Fixtures;
using DumpDetective.Tests.Scenarios.Thread;

namespace DumpDetective.Tests.Integration;

public sealed class DeadlockTest(CommandContext<DeadlockScenario> ctx)
    : ScenarioTestBase<DeadlockScenario>(ctx), IClassFixture<CommandContext<DeadlockScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
