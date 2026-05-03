using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Gc;

// DdStaticAnchors.Root → DdStaticHolder graph.
// Test asserts: "DdStaticHolder" substring in table.
//
// DdStaticAnchors MUST be a regular (non-static) class so ClrMD's
// GetTypeByMethodTable() can find it.  ClrMD 3.1.x builds its method-table
// cache lazily from heap objects; for purely static classes with no heap
// instances, the method table is never added to the cache and the static
// field Root is silently skipped.  By storing one DdStaticAnchors instance
// in a static field we guarantee the type is found during the heap walk.

/// <summary>
/// Public anchor — kept as a regular (non-static) class so a heap instance
/// can be created, making ClrMD's GetTypeByMethodTable succeed for it
/// during the static-field scan in StaticRootEntries.Build().
/// </summary>
public sealed class DdStaticAnchors
{
    /// <summary>Instance field forces a heap allocation so ClrMD can resolve the method table.</summary>
    public string AnchorId;

    public DdStaticAnchors(string id) => AnchorId = id;

    /// <summary>The planted static reference root — ClrMD reads this in StaticRootEntries.</summary>
    public static DdStaticHolder? Root;
}

/// <summary>Root of the planted graph — "DdStaticHolder" matches the test assertion.</summary>
public sealed class DdStaticHolder(string tag)
{
    public string             Tag      = tag;
    public List<DdStaticNode> Children = [];
}

/// <summary>Child node in the planted graph.</summary>
public sealed class DdStaticNode(string name, byte[] data)
{
    public string Name = name;
    public byte[] Data = data;
}

public sealed class StaticRefsScenario : IScenario
{
    // Keep a live heap instance of DdStaticAnchors so ClrMD can find its
    // method table via heap walk, enabling GetTypeByMethodTable to succeed.
    private static DdStaticAnchors? _anchor;

    public string CommandName => "static-refs";
    public string Description => "DdStaticHolder graph (100 nodes) anchored via DdStaticAnchors.Root public static field.";

    public void Setup()
    {
        // Allocate the anchor — this puts a DdStaticAnchors instance on the heap
        // so ClrMD can resolve its method table and enumerate its static fields.
        _anchor = new DdStaticAnchors("test-anchor-1");

        var root = new DdStaticHolder("test-static-root");
        for (int i = 0; i < 100; i++)
            root.Children.Add(new DdStaticNode($"node-{i}", new byte[512]));
        DdStaticAnchors.Root = root;
    }

    public void Teardown()
    {
        DdStaticAnchors.Root = null;
        _anchor = null;
    }

    public void Validate(ReportDoc doc)
    {
        DocAssert.HasSection(doc, "Non-Null Static Reference Fields");
        var tables = DocAssert.AllTables(doc);
        bool hasData = tables.Any(t => t.Rows.Count > 0);
        if (hasData)
            DocAssert.AnyTableContainsText(doc, "DdStaticHolder",
                "DdStaticAnchors.Root should reference a DdStaticHolder — appears in Value Type column");
        DocAssert.HasContent(doc);
    }
}
