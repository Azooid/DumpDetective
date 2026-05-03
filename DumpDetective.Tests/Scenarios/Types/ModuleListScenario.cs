using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;

namespace DumpDetective.Tests.Scenarios.Types;

public sealed class ModuleListScenario : IScenario
{
    public string CommandName => "module-list";
    public string Description => "No setup needed — every .NET process has loaded modules.";

    public void Setup() { /* modules are always present in any .NET process */ }

    public void Validate(ReportDoc doc)
    {
        // Loaded modules section is always present
        DocAssert.HasSection(doc, "Loaded Modules");

        // Module list table: [Assembly, Kind, Size, Path]
        // Any .NET process has many loaded assemblies (coreclr, runtime, test framework...)
        DocAssert.TableByHeadersHasMinRows(doc, 5, "loaded modules table",
            "Assembly", "Kind", "Size", "Path");

        // System.Private.CoreLib is loaded in every .NET process
        // (replaces the former "xunit" assertion which is absent in ScenarioHost mode)
        DocAssert.AnyTableContainsText(doc, "System.Private.CoreLib",
            "System.Private.CoreLib must be listed in every .NET process");
    }
}
