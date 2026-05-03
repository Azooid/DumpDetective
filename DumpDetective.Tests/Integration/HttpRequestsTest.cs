using DumpDetective.Tests.Fixtures;
using DumpDetective.Tests.Scenarios.Async;

namespace DumpDetective.Tests.Integration;

public sealed class HttpRequestsTest(CommandContext<HttpRequestsScenario> ctx)
    : ScenarioTestBase<HttpRequestsScenario>(ctx), IClassFixture<CommandContext<HttpRequestsScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
