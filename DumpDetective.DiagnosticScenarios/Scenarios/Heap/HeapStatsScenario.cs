using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Heap;

// 500 instances × 5 custom types.  Test asserts: substring "HsType".

file sealed record HsTypeA(int Id);
file sealed record HsTypeB(string Label, int Value);
file sealed record HsTypeC(int X, int Y, int Z);
file sealed record HsTypeD(byte[] Data);
file sealed record HsTypeE(int Bucket);

public sealed class HeapStatsScenario : IScenario
{
    private static readonly List<object> _objects = [];
    public string CommandName => "heap-stats";
    public string Description => "500 instances × 5 custom types on heap.";

    public void Setup()
    {
        const int n = 500;
        for (int i = 0; i < n; i++) _objects.Add(new HsTypeA(i));
        for (int i = 0; i < n; i++) _objects.Add(new HsTypeB($"item-{i}", i * 2));
        for (int i = 0; i < n; i++) _objects.Add(new HsTypeC(i, i + 1, i + 2));
        for (int i = 0; i < n; i++) _objects.Add(new HsTypeD(new byte[64]));
        for (int i = 0; i < n; i++) _objects.Add(new HsTypeE(i % 10));
    }

    public void Validate(ReportDoc doc)
    {
        DocAssert.HasSection(doc, "Heap Statistics");
        DocAssert.TableByHeadersHasMinRows(doc, 10, "heap type table",
            "Type", "Gen", "Count", "Total Size", "% of Heap");
        DocAssert.HasKeyValue(doc, "Total objects");
        DocAssert.HasKeyValue(doc, "Total size");
        DocAssert.AnyTableContainsText(doc, "System.String",
            "System.String is always present on a managed heap");
        DocAssert.AnyTableContainsText(doc, "HsType",
            "our 5 planted HsType record families must appear");
    }
}
