using DumpDetective.Tests.Fixtures;
using DumpDetective.Tests.Scenarios.Leaks;

namespace DumpDetective.Tests.Integration;

public sealed class TimerLeaksTest(CommandContext<TimerLeaksScenario> ctx)
    : ScenarioTestBase<TimerLeaksScenario>(ctx), IClassFixture<CommandContext<TimerLeaksScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
