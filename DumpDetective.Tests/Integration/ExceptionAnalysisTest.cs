using DumpDetective.Tests.Fixtures;
using DumpDetective.DiagnosticScenarios;
using DumpDetective.DiagnosticScenarios.Scenarios.Exceptions;

namespace DumpDetective.Tests.Integration;

public sealed class ExceptionAnalysisTest(CommandContext<ExceptionAnalysisScenario> ctx)
    : ScenarioTestBase<ExceptionAnalysisScenario>(ctx), IClassFixture<CommandContext<ExceptionAnalysisScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
