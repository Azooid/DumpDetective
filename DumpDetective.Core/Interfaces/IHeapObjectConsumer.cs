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

    /// <summary>
    /// When <see langword="true"/>, <c>HeapWalker</c> shares this consumer instance directly
    /// across all parallel bucket workers instead of cloning it. The implementation is
    /// responsible for thread-safe accumulation (e.g. via <c>ConcurrentDictionary</c> or
    /// <c>Interlocked</c>). <see cref="CreateClone"/> and <see cref="MergeFrom"/> are never
    /// called for thread-safe consumers.
    /// Use this for consumers whose clone would be prohibitively large (e.g. InboundRefConsumer
    /// with ~80 M entries × 8 clones = ~30 GB).
    /// </summary>
    bool IsThreadSafe => false;

    /// <summary>
    /// Returns a fresh, empty instance of the same consumer type.
    /// Never called when <see cref="IsThreadSafe"/> is <see langword="true"/>.
    /// </summary>
    IHeapObjectConsumer CreateClone();

    /// <summary>
    /// Merges accumulated state from <paramref name="other"/> (a clone created by
    /// <see cref="CreateClone"/>) into this master instance.
    /// Never called when <see cref="IsThreadSafe"/> is <see langword="true"/>.
    /// </summary>
    void MergeFrom(IHeapObjectConsumer other);
}
