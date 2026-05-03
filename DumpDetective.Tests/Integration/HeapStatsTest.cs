using DumpDetective.Tests.Fixtures;
using DumpDetective.Tests.Scenarios.Heap;

namespace DumpDetective.Tests.Integration;

public sealed class HeapStatsTest(CommandContext<HeapStatsScenario> ctx)
    : ScenarioTestBase<HeapStatsScenario>(ctx), IClassFixture<CommandContext<HeapStatsScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
