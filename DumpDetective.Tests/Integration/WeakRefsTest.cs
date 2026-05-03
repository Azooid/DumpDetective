using DumpDetective.Tests.Fixtures;
using DumpDetective.Tests.Scenarios.Gc;

namespace DumpDetective.Tests.Integration;

public sealed class WeakRefsTest(CommandContext<WeakRefsScenario> ctx)
    : ScenarioTestBase<WeakRefsScenario>(ctx), IClassFixture<CommandContext<WeakRefsScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
