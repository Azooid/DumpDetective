using DumpDetective.Tests.Fixtures;
using DumpDetective.Tests.Scenarios.Heap;

namespace DumpDetective.Tests.Integration;

public sealed class MemoryLeakTest(CommandContext<MemoryLeakScenario> ctx)
    : ScenarioTestBase<MemoryLeakScenario>(ctx), IClassFixture<CommandContext<MemoryLeakScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
