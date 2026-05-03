using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;
using System.Runtime.InteropServices;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Gc;

// 90 GCHandles: 30 Normal + 30 Weak + 30 WeakTrackResurrection.
// Test asserts: "DdHandleTarget" substring.

// Public so ClrMD reports it as DdHandleTarget (not an opaque internal type).
public sealed class DdHandleTarget(int Id) { public int Id = Id; }

public sealed class HandleTableScenario : IScenario
{
    private static readonly List<GCHandle> _handles = [];
    public string CommandName => "handle-table";
    public string Description => "90 GCHandles: 30 Normal (DdHandleTarget) + 30 Weak + 30 WeakTrackResurrection.";

    public void Setup()
    {
        for (int i = 0; i < 30; i++) _handles.Add(GCHandle.Alloc(new DdHandleTarget(i),      GCHandleType.Normal));
        for (int i = 0; i < 30; i++) _handles.Add(GCHandle.Alloc(new DdHandleTarget(i + 30), GCHandleType.Weak));
        for (int i = 0; i < 30; i++) _handles.Add(GCHandle.Alloc(new DdHandleTarget(i + 60), GCHandleType.WeakTrackResurrection));
    }

    public void Validate(ReportDoc doc)
    {
        DocAssert.HasSection(doc, "Handle Summary");
        DocAssert.TableByHeadersHasMinRows(doc, 3, "handle kind table",
            "Handle Kind", "Count", "Referenced Size");
        DocAssert.AnyTableContainsText(doc, "Strong",
            "GCHandleType.Normal maps to 'Strong' in ClrMD (30 handles registered)");
        DocAssert.AnyTableContainsText(doc, "Weak",
            "GCHandleType.Weak handles we registered (30)");
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
