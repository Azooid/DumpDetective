using DumpDetective.Tests.Fixtures;
using DumpDetective.Tests.Scenarios.Gc;

namespace DumpDetective.Tests.Integration;

public sealed class FinalizerQueueTest(CommandContext<FinalizerQueueScenario> ctx)
    : ScenarioTestBase<FinalizerQueueScenario>(ctx), IClassFixture<CommandContext<FinalizerQueueScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
