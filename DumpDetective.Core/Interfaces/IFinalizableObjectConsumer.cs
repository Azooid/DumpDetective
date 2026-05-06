using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Core.Interfaces;

/// <summary>
/// A single-responsibility accumulator registered with <c>HeapWalker.Walk</c>
/// for the <em>finalizable-object</em> pass.
///
/// <para>
/// After <see cref="IHeapObjectConsumer.OnWalkComplete"/> completes for all
/// heap-object consumers, <c>HeapWalker</c> calls
/// <see cref="ConsumeFinalizableObject"/> once per object returned by
/// <c>ClrHeap.EnumerateFinalizableObjects()</c>, then calls
/// <see cref="OnFinalizableQueueComplete"/> on every registered consumer.
/// </para>
///
/// <para>
/// The finalizable queue is typically very small (≪ 1 M objects) so the pass
/// runs sequentially on the caller thread — no cloning or merging is required.
/// </para>
/// </summary>
public interface IFinalizableObjectConsumer
{
    /// <summary>
    /// Called once per object in the GC finalizer queue.
    /// </summary>
    void ConsumeFinalizableObject(in ClrObject obj, ClrHeap heap);

    /// <summary>
    /// Called exactly once after all objects in the finalizable queue have been
    /// dispatched (even if <c>EnumerateFinalizableObjects</c> throws).
    /// Use this to assemble the final result from accumulated state.
    /// </summary>
    void OnFinalizableQueueComplete();
}
