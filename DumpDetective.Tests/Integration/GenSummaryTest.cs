using DumpDetective.Tests.Fixtures;
using DumpDetective.Tests.Scenarios.Heap;

namespace DumpDetective.Tests.Integration;

public sealed class GenSummaryTest(CommandContext<GenSummaryScenario> ctx)
    : ScenarioTestBase<GenSummaryScenario>(ctx), IClassFixture<CommandContext<GenSummaryScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
