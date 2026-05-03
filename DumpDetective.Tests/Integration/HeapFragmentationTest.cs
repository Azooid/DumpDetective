using DumpDetective.Tests.Fixtures;
using DumpDetective.DiagnosticScenarios;
using DumpDetective.DiagnosticScenarios.Scenarios.Heap;

namespace DumpDetective.Tests.Integration;

public sealed class HeapFragmentationTest(CommandContext<HeapFragmentationScenario> ctx)
    : ScenarioTestBase<HeapFragmentationScenario>(ctx), IClassFixture<CommandContext<HeapFragmentationScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
