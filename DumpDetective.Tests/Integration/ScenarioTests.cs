using DumpDetective.Cli;
using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;

namespace DumpDetective.Tests.Integration;

/// <summary>
/// Base class for all scenario integration tests.
///
/// Each derived class declares <c>IClassFixture&lt;CommandContext&lt;TScenario&gt;&gt;</c>,
/// which causes xUnit to create the fixture once per class (one <c>DumpContext.Open</c>
/// + <c>BuildReport</c>) and share it across all <c>[Fact]</c> methods.
///
/// <c>[Collection("Scenarios")]</c> is inherited, tying every derived class to the
/// shared <see cref="ScenarioFixture"/> (dump capture / teardown).
///
/// Pattern:
/// <code>
/// public sealed class HeapStatsTest(CommandContext&lt;HeapStatsScenario&gt; ctx)
///     : ScenarioTestBase&lt;HeapStatsScenario&gt;(ctx),
///       IClassFixture&lt;CommandContext&lt;HeapStatsScenario&gt;&gt;
/// {
///     [Fact] public void Report_HasContent()   => DocAssert.HasContent(Doc);
///     [Fact] public void Scenario_Validates()  => Scenario.Validate(Doc);
///     // Add more [Fact] methods here as assertions grow
/// }
/// </code>
/// </summary>
[Collection("Scenarios")]
public abstract class ScenarioTestBase<TScenario>(CommandContext<TScenario> ctx)
    where TScenario : IScenario, new()
{
    /// <summary>The report document built once for this test class.</summary>
    protected ReportDoc Doc => ctx.Doc;

    /// <summary>The scenario instance (for calling <c>Validate</c> or reading metadata).</summary>
    protected TScenario Scenario => ctx.Scenario;

    /// <summary>
    /// Writes a one-line summary to the test output window so each command's
    /// section/table/alert counts are visible without opening the HTML report.
    /// </summary>
    [Fact]
    public void Report_Summary()
    {
        int sections = DocAssert.AllSections(Doc).Count();
        var tables   = DocAssert.AllTables(Doc);
        int alerts   = DocAssert.AllAlerts(Doc).Count();
        string path  = ctx.HtmlReportPath ?? "(not written)";

        Console.WriteLine($"Command  : {Scenario.CommandName}");
        Console.WriteLine($"Scenario : {Scenario.Description}");
        Console.WriteLine($"Sections : {sections}");
        Console.WriteLine($"Tables   : {tables.Count}  (rows: {string.Join(", ", tables.Select(t => t.Rows.Count))})");
        Console.WriteLine($"Alerts   : {alerts}");
        Console.WriteLine($"HTML     : {path}");

        // The summary fact is always a pass — it only prints info.
        Assert.True(true);
    }
}
