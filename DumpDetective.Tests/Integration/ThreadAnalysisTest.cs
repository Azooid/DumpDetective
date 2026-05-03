using DumpDetective.Tests.Fixtures;
using DumpDetective.Tests.Scenarios.Thread;

namespace DumpDetective.Tests.Integration;

public sealed class ThreadAnalysisTest(CommandContext<ThreadAnalysisScenario> ctx)
    : ScenarioTestBase<ThreadAnalysisScenario>(ctx), IClassFixture<CommandContext<ThreadAnalysisScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
