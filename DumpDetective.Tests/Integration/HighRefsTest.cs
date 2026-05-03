using DumpDetective.Tests.Fixtures;
using DumpDetective.Tests.Scenarios.Heap;

namespace DumpDetective.Tests.Integration;

public sealed class HighRefsTest(CommandContext<HighRefsScenario> ctx)
    : ScenarioTestBase<HighRefsScenario>(ctx), IClassFixture<CommandContext<HighRefsScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
