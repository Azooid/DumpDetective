using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class ModuleListReport
{
    public void Render(ModuleListData data, IRenderSink sink)
    {
        var duplicates = data.Modules
            .GroupBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        if (duplicates.Count > 0)
            sink.Alert(AlertLevel.Warning,
                $"{duplicates.Count} assembly name(s) loaded from multiple paths.",
                "Duplicate assemblies can cause type identity mismatches and unexpected behavior.",
                "Ensure only one version of each assembly is deployed. Check binding redirects.");

        RenderModuleTable(data, sink, duplicates.Count);
        if (duplicates.Count > 0) RenderDuplicateAccordions(duplicates, sink);
    }

    private static void RenderModuleTable(ModuleListData data, IRenderSink sink, int dupCount)
    {
        sink.Section("Loaded Modules");
        sink.Explain(
            what: "Lists all managed assemblies (modules) loaded into the process, classified by origin: App, System/framework, or GAC.",
            why: "Duplicate assembly names loaded from different paths cause type identity mismatches: two 'identical' types from different load paths are not equal.",
            impact: "Type cast failures, interface mismatch exceptions, and serialization surprises can all stem from having multiple versions of the same assembly loaded.",
            bullets: ["'App' modules = your code and third-party packages", "'System' modules = .NET runtime framework assemblies", "Duplicate names in 'Duplicate Assemblies' section are the highest priority to investigate"],
            action: "Ensure binding redirects in app.config point to a single version. Audit multi-targeting or plugin loading scenarios where assemblies are loaded from different directories."
        );
        var rows = data.Modules
            .Select(m => new[] { m.FileName, m.Kind, DumpHelpers.FormatSize(m.Size), m.Path })
            .ToList();
        sink.Table(["Assembly", "Kind", "Size", "Path"], rows, $"{rows.Count} module(s)");

        sink.KeyValues([
            ("Total modules",   data.Modules.Count.ToString("N0")),
            ("App modules",     data.Modules.Count(m => m.Kind == "App").ToString("N0")),
            ("System modules",  data.Modules.Count(m => m.Kind == "System").ToString("N0")),
            ("GAC modules",     data.Modules.Count(m => m.Kind == "GAC").ToString("N0")),
            ("Duplicate names", dupCount.ToString("N0")),
        ]);
    }

    private static void RenderDuplicateAccordions(
        IEnumerable<IGrouping<string, ModuleItem>> duplicates, IRenderSink sink)
    {
        sink.Section("Duplicate Assemblies");
        foreach (var dup in duplicates)
        {
            sink.BeginDetails($"{dup.Key}  — {dup.Count()} copies", open: true);
            var rows = dup.Select(m => new[] { m.Kind, DumpHelpers.FormatSize(m.Size), m.Path }).ToList();
            sink.Table(["Kind", "Size", "Path"], rows);
            sink.EndDetails();
        }
    }
}
