using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;
using System.Runtime.InteropServices;

namespace DumpDetective.Tests.Scenarios.Gc;

public sealed class HandleTableScenario : IScenario
{
    /// <summary>
    /// Named test type — shows as "DdHandleTarget" in the report’s object-type column
    /// instead of the opaque "System.Object", making it easy to verify in the HTML output.
    /// </summary>
    private sealed class DdHandleTarget(int Id)
    {
        public int Id { get; } = Id;
    }

    private static readonly List<GCHandle> _handles = [];

    public string CommandName => "handle-table";
    public string Description => "90 GCHandles: 30 Normal (DdHandleTarget) + 30 Weak + 30 WeakTrackResurrection.";

    public void Setup()
    {
        for (int i = 0; i < 30; i++) _handles.Add(GCHandle.Alloc(new DdHandleTarget(i),       GCHandleType.Normal));
        for (int i = 0; i < 30; i++) _handles.Add(GCHandle.Alloc(new DdHandleTarget(i + 30),  GCHandleType.Weak));
        for (int i = 0; i < 30; i++) _handles.Add(GCHandle.Alloc(new DdHandleTarget(i + 60),  GCHandleType.WeakTrackResurrection));
    }

    public void Validate(ReportDoc doc)
    {
        // Summary section must be present
        DocAssert.HasSection(doc, "Handle Summary");

        // Handle kind table: [Handle Kind, Count, Referenced Size]
        // We registered 3 distinct kinds, so ≥ 3 rows
        DocAssert.TableByHeadersHasMinRows(doc, 3, "handle kind table",
            "Handle Kind", "Count", "Referenced Size");

        // GCHandleType.Normal → ClrMD calls it "Strong"
        DocAssert.AnyTableContainsText(doc, "Strong",
            "GCHandleType.Normal maps to 'Strong' in ClrMD (30 handles registered)");

        // GCHandleType.Weak → ClrMD calls it "Weak" / "WeakShort"
        DocAssert.AnyTableContainsText(doc, "Weak",
            "GCHandleType.Weak handles we registered (30)");

        // Per-kind type breakdown must list our identifiable DdHandleTarget type
        DocAssert.AnyTableContainsText(doc, "DdHandleTarget",
            "our 90 DdHandleTarget objects must appear in the per-kind type breakdown");
    }

    public void Teardown()
    {
        foreach (var h in _handles)
            if (h.IsAllocated) h.Free();
        _handles.Clear();
    }
}
