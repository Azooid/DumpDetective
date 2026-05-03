using DumpDetective.Tests.Fixtures;
using DumpDetective.Tests.Scenarios.Gc;

namespace DumpDetective.Tests.Integration;

public sealed class StaticRefsTest(CommandContext<StaticRefsScenario> ctx)
    : ScenarioTestBase<StaticRefsScenario>(ctx), IClassFixture<CommandContext<StaticRefsScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
