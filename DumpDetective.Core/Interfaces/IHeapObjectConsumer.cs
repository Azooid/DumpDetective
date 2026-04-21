using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Core.Interfaces;

/// <summary>
/// A single-responsibility accumulator registered with <c>HeapWalker.Walk</c>.
/// Each implementation is called once per live heap object during the single
/// <c>heap.EnumerateObjects()</c> pass.
/// </summary>
public interface IHeapObjectConsumer
{
    /// <summary>
    /// Called once per valid, non-free heap object.
    /// Implementations MUST NOT allocate on the hot path — use
    /// <c>CollectionsMarshal.GetValueRefOrAddDefault</c> for dictionary updates.
    /// </summary>
    void Consume(in ClrObject obj, Runtime.HeapTypeMeta meta, ClrHeap heap);

    /// <summary>
    /// Called exactly once after the walk completes (even if the walk throws).
    /// Use this to finalise any derived data from accumulated state.
    /// </summary>
    void OnWalkComplete();
}
