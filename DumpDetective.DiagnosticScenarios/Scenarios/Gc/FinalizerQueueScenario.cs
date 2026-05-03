using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;
using System.Runtime.CompilerServices;
using SysThread = System.Threading.Thread;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Gc;

// Finalizer thread blocked; 200 DdFinalizableItem objects queued behind it.
// Test asserts: "DdFinalizableItem" in type table, Total in queue ≥ 200.

public sealed class FinalizerQueueScenario : IScenario
{
    private static readonly ManualResetEventSlim _gate           = new(false);
    private static readonly ManualResetEventSlim _blockerStarted = new(false);
    private static bool _blockerQueued;

    public string CommandName => "finalizer-queue";
    public string Description => "200 DdFinalizableItem objects behind a blocked finalizer thread.";

    public void Setup()
    {
        if (!_blockerQueued)
        {
            _ = new DdFinalizerBlocker(_gate, _blockerStarted);
            _blockerQueued = true;
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        _blockerStarted.Wait(TimeSpan.FromSeconds(10));
        SysThread.Sleep(20);

        AllocateItems(200);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateItems(int count)
    {
        var items = new DdFinalizableItem[count];
        for (int i = 0; i < count; i++)
            items[i] = new DdFinalizableItem(i, _gate);
        GC.KeepAlive(items);
    }

    public void Teardown() => _gate.Set();

    public void Validate(ReportDoc doc)
    {
        DocAssert.HasSection(doc, "Finalizer Queue Summary");
        DocAssert.HasSection(doc, "Types by Queue Size");
        DocAssert.HasKeyValue(doc, "Total in queue");
        var queueSizeStr = DocAssert.GetKeyValue(doc, "Total in queue");
        Assert.True(
            queueSizeStr is not null &&
            long.TryParse(queueSizeStr.Replace(",", ""), out long qSize) && qSize >= 200,
            $"Expected 'Total in queue' ≥ 200 (planted 200 DdFinalizableItem). Got: '{queueSizeStr}'");
        DocAssert.AnyTableContainsText(doc, "DdFinalizableItem",
            "our planted DdFinalizableItem objects must appear in the queue type table");
    }

    private sealed class DdFinalizerBlocker(ManualResetEventSlim gate, ManualResetEventSlim started)
    {
        ~DdFinalizerBlocker()
        {
            started.Set();
            gate.Wait(TimeSpan.FromSeconds(60));
        }
    }
}

// Public so ClrMD can enumerate its method table from the heap.
// Name "DdFinalizableItem" matches the test's AnyTableContainsText assertion.
public sealed class DdFinalizableItem(int id, ManualResetEventSlim gate)
{
    private readonly int _id = id;
    ~DdFinalizableItem()
    {
        gate.Wait(TimeSpan.FromSeconds(60));
        GC.KeepAlive(_id);
    }
}
