using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Types;

// Every .NET process has loaded modules.  No setup needed.
// NOTE: the test assertion uses "System.Private.CoreLib" (present in every .NET process)
//       rather than "xunit" which is absent from this project's assemblies.

public sealed class ModuleListScenario : IScenario
{
    public string CommandName => "module-list";
    public string Description => "No setup needed — every .NET process has loaded modules.";
    public void   Setup() { }

    public void Validate(ReportDoc doc)
    {
        DocAssert.HasSection(doc, "Loaded Modules");
        DocAssert.TableByHeadersHasMinRows(doc, 5, "loaded modules table",
            "Assembly", "Kind", "Size", "Path");
        DocAssert.AnyTableContainsText(doc, "System.Private.CoreLib",
            "System.Private.CoreLib must be listed in every .NET process");
    }
}
