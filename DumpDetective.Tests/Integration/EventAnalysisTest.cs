using DumpDetective.Tests.Fixtures;
using DumpDetective.Tests.Scenarios.Exceptions;

namespace DumpDetective.Tests.Integration;

public sealed class EventAnalysisTest(CommandContext<EventAnalysisScenario> ctx)
    : ScenarioTestBase<EventAnalysisScenario>(ctx), IClassFixture<CommandContext<EventAnalysisScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
