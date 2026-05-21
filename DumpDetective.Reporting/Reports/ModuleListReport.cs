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
        RenderSymbolsSection(data, sink);
        RenderAlcSection(data, sink);
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

        sink.KeyValues([
            ("Total modules",   data.Modules.Count.ToString("N0")),
            ("App modules",     data.Modules.Count(m => m.Kind == "App").ToString("N0")),
            ("System modules",  data.Modules.Count(m => m.Kind == "System").ToString("N0")),
            ("GAC modules",     data.Modules.Count(m => m.Kind == "GAC").ToString("N0")),
            ("Duplicate names", dupCount.ToString("N0")),
        ]);

        // Module kind breakdown donut
        {
            var kindSegs = data.Modules
                .GroupBy(m => m.Kind.Length > 0 ? m.Kind : "Other")
                .Select(g => (Label: g.Key, Value: (double)g.Count()))
                .OrderByDescending(s => s.Value)
                .ToList();
            if (kindSegs.Count > 1)
                sink.DonutChart(kindSegs, "Modules by kind",
                    $"{data.Modules.Count:N0}\nmodules");
        }

        var rows = data.Modules
            .Select(m => new[] { m.FileName, m.Kind, DumpHelpers.FormatSize(m.Size), m.Path })
            .ToList();
        sink.Table(["Assembly", "Kind", "Size", "Path"], rows, $"{rows.Count} module(s)");
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

    private static void RenderSymbolsSection(ModuleListData data, IRenderSink sink)
    {
        // Only show PDB section when at least one module has PDB metadata.
        var withPdb    = data.Modules.Where(m => m.PdbGuid is not null).ToList();
        if (withPdb.Count == 0) return;

        int hasPdbFile = withPdb.Count(m => m.PdbPresent);
        int noPdbFile  = withPdb.Count(m => !m.PdbPresent);

        sink.Section("Symbol (PDB) Metadata");
        sink.Explain(
            what: "PDB identity (GUID + age) for every loaded module that has debug information embedded in its PE header.",
            why:  "These values are required for symbol-server lookups. The GUID and age must match exactly — " +
                  "a PDB compiled from a different build will not match and symbols will not resolve.",
            bullets: [
                "PDB Path = path recorded in the PE at build time; may not exist on this machine",
                "GUID = unique build identity; must match the symbol server index exactly",
                "Age = PDB revision counter; incremented each time the binary is re-linked",
                "Present = PDB file found at the recorded path on this machine",
            ],
            action: "To resolve symbols: use WinDbg .sympath or set _NT_SYMBOL_PATH=srv*<local-cache>*https://msdl.microsoft.com/download/symbols. " +
                    "For private symbols use your team's symbol server URL."
        );

        sink.KeyValues([
            ("Modules with PDB metadata", withPdb.Count.ToString("N0")),
            ("PDB file present locally",  hasPdbFile.ToString("N0")),
            ("PDB file missing locally",  noPdbFile.ToString("N0")),
        ]);

        if (noPdbFile > 0)
            sink.Alert(AlertLevel.Info,
                $"{noPdbFile} module(s) have PDB metadata but the PDB file was not found at the recorded path.",
                "Stack frames for these modules will show addresses rather than method names in native debuggers.",
                "Configure a symbol server or copy PDB files alongside the dump to enable symbol resolution.");

        var rows = withPdb
            .OrderBy(m => m.Kind == "App" ? 0 : 1)
            .ThenBy(m => m.FileName)
            .Select(m => new[]
            {
                m.FileName,
                m.Kind,
                m.PdbGuid ?? string.Empty,
                m.PdbAge.ToString(),
                m.PdbPresent ? "✓" : "✗",
                m.PdbPath ?? string.Empty,
            })
            .ToList();
        sink.Table(["Assembly", "Kind", "PDB GUID", "Age", "Present", "PDB Path"], rows,
            $"Symbol identity for {withPdb.Count} module(s)");
    }

    private static void RenderAlcSection(ModuleListData data, IRenderSink sink)
    {
        if (data.AssemblyLoadContexts is not { Count: > 0 }) return;

        sink.Section("Assembly Load Contexts");
        sink.Explain(
            what: "AssemblyLoadContext (ALC) instances found on the managed heap. Each ALC is an isolation boundary — " +
                  "assemblies loaded into different contexts cannot share types, even if the assembly name and version match.",
            why:  "Plugin systems, hot-reload scenarios, and dependency injection containers sometimes create multiple ALCs. " +
                  "Collectible ALCs (marked IsCollectible=true) should be collected once all references to their types are released. " +
                  "Non-collectible ALCs live for the lifetime of the process.",
            bullets:
            [
                "IsCollectible=false + many assemblies \u2192 permanent memory tied to this ALC for the process lifetime",
                "IsCollectible=true \u2192 intended to be unloaded; confirm references are released after plugin teardown",
                "Multiple ALCs with the same name \u2192 may indicate repeated plugin load without unload (ALC leak)",
            ],
            action: "For collectible ALCs: ensure no GC handles or static fields reference types from the ALC. " +
                    "Use WeakReference<AssemblyLoadContext> to monitor unloading. " +
                    "Call AssemblyLoadContext.Unload() explicitly when done.");

        int collectibleCount    = data.AssemblyLoadContexts.Count(a => a.IsCollectible);
        int nonCollectibleCount = data.AssemblyLoadContexts.Count(a => !a.IsCollectible);

        sink.KeyValues([
            ("Total ALCs",      data.AssemblyLoadContexts.Count.ToString("N0")),
            ("Collectible",     collectibleCount.ToString("N0")),
            ("Non-collectible", nonCollectibleCount.ToString("N0")),
        ]);

        // Warn about multiple ALCs with the same name (possible ALC leak)
        var nameDups = data.AssemblyLoadContexts
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();
        if (nameDups.Count > 0)
            sink.Alert(AlertLevel.Warning,
                $"{nameDups.Count} ALC name(s) have multiple instances — possible AssemblyLoadContext leak.",
                advice: "Each repeated ALC with the same name suggests a plugin was re-loaded without unloading the previous instance. " +
                        "Ensure Unload() is called and awaited before reloading.");

        var alcRows = data.AssemblyLoadContexts
            .OrderByDescending(a => a.AssemblyCount)
            .Select(a => new[]
            {
                $"0x{a.Address:X}",
                a.Name.Length > 50 ? a.Name[..50] + "\u2026" : a.Name,
                a.IsCollectible ? "Yes" : "No",
                a.AssemblyCount.ToString("N0"),
            })
            .ToList();

        sink.Table(
            ["Address", "ALC Name", "Collectible", "Assembly Count"],
            alcRows,
            "Assembly Count = number of assemblies loaded into this context at time of dump.");
    }
}
