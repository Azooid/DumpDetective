using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;
using System.Runtime.CompilerServices;

namespace DumpDetective.Tests.Scenarios.Gc;

/// <summary>
/// Blocks the finalizer thread so 200 named DdFinalizableItem objects
/// accumulate in the f-reachable queue for the dump to capture.
///
/// Two-gate pattern:
///   _blockerStarted — set by FinalizerBlocker's finalizer the instant it starts running,
///                     so Setup() waits for a guaranteed "finalizer thread is blocked" signal.
///   _gate           — keeps the finalizer thread stalled until Teardown() releases it.
///
/// DdFinalizableItem also waits on _gate, so if FinalizerBlocker somehow completes,
/// the first item's finalizer stalls and items 1-199 stay in the f-reachable queue.
/// </summary>
public sealed class FinalizerQueueScenario : IScenario
{
    private static readonly ManualResetEventSlim _gate           = new(false);
    private static readonly ManualResetEventSlim _blockerStarted = new(false);
    private static bool _blockerQueued;

    public string CommandName => "finalizer-queue";
    public string Description => "Finalizer thread blocked on a gate; 200 DdFinalizableItem objects queued behind it.";

    public void Setup()
    {
        if (!_blockerQueued)
        {
            _ = new FinalizerBlocker(_gate, _blockerStarted);
            _blockerQueued = true;
        }

        // Collect the blocker so its finalizer starts running on the finalizer thread
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);

        // Wait until the finalizer thread confirms it has started (and is about to block)
        _blockerStarted.Wait(TimeSpan.FromSeconds(10));
        // Tiny pause so the finalizer thread has entered _gate.Wait() before we proceed
        System.Threading.Thread.Sleep(20);

        // Create 200 items in a NoInlining scope so the JIT can't treat them as dead-on-creation
        AllocateFinalizableItems(200);

        // Force-collect so all 200 items move to the f-reachable queue.
        // The finalizer thread is blocked on _gate; they pile up behind it.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
    }

    public void Validate(ReportDoc doc)
    {
        // Both structural sections must be present
        DocAssert.HasSection(doc, "Finalizer Queue Summary");
        DocAssert.HasSection(doc, "Types by Queue Size");

        // KV "Total in queue" must be present AND show ≥ 200 (we planted 200)
        DocAssert.HasKeyValue(doc, "Total in queue");
        var queueSizeStr = DocAssert.GetKeyValue(doc, "Total in queue");
        Assert.True(
            queueSizeStr is not null &&
            long.TryParse(queueSizeStr.Replace(",", ""), out long qSize) && qSize >= 200,
            $"Expected 'Total in queue' ≥ 200 (planted 200 DdFinalizableItem). Got: '{queueSizeStr}'");

        // DdFinalizableItem is the planted type — it must appear in the types table
        DocAssert.AnyTableContainsText(doc, "DdFinalizableItem",
            "our planted DdFinalizableItem objects must appear in the queue type table");
    }

    public void Teardown()
    {
        _gate.Set(); // release the finalizer thread and all blocked DdFinalizableItem finalizers
    }

    // ── Supporting types ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates <paramref name="count"/> finalizable items inside a non-inlineable
    /// scope. The local array keeps all items rooted until the method returns,
    /// after which a GC can safely move them to the f-reachable queue.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateFinalizableItems(int count)
    {
        var items = new DdFinalizableItem[count];
        for (int i = 0; i < count; i++)
            items[i] = new DdFinalizableItem(i, _gate);
        GC.KeepAlive(items);
    }

    private sealed class FinalizerBlocker(ManualResetEventSlim gate, ManualResetEventSlim started)
    {
        ~FinalizerBlocker()
        {
            started.Set();                       // signal: finalizer thread is running
            gate.Wait(TimeSpan.FromSeconds(60)); // block the finalizer thread
        }
    }

    /// <summary>
    /// Named test type — "DdFinalizableItem" is easily spotted in the report's type table.
    /// Its finalizer also waits on <paramref name="gate"/> so even if FinalizerBlocker
    /// completes, the first item stalls and items 1-199 stay in the f-reachable queue.
    /// </summary>
    private sealed class DdFinalizableItem(int id, ManualResetEventSlim gate)
    {
        private readonly int _id = id;
        ~DdFinalizableItem()
        {
            gate.Wait(TimeSpan.FromSeconds(60));
            GC.KeepAlive(_id);
        }
    }
}
