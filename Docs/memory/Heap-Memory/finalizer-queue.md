# finalizer-queue

**Category:** Heap / Memory  
**Included in `analyze --full`:** Yes

## What it does

Analyzes the GC finalizer queue — objects waiting to be finalized. Reports which types are pending finalization, total count and retained size, whether the finalizer thread is currently blocked, and resurrection candidates (objects re-rooted by their finalizer). A large finalizer queue is a warning sign: unmanaged resources (file handles, sockets, database connections) held by these objects will not be released until finalization completes.

---

## Analyzer: `FinalizerQueueAnalyzer`

**Class type:** Not `IHeapObjectConsumer` — reads finalizer queue and thread structures directly  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

### How it works

The analyzer runs three phases sequentially.

**Phase 1 — Finalizer thread state.** Finds the finalizer thread in `ctx.Runtime.Threads` by `t.IsFinalizer == true`, reads its managed and OS thread IDs, and captures the top 10 stack frames via `t.EnumerateStackTrace()`. The thread is considered blocked when any frame name contains `WaitOne`, `Sleep`, `ManualResetEvent`, or `Monitor.Wait`, and no frame contains `WaitForWork` (the normal idle state). This heuristic catches the common pattern where the finalizer is stuck waiting on a lock held by a thread that is waiting on the finalizer.

**Phase 2 — Queue scan.** Calls `ctx.Heap.EnumerateFinalizableObjects()` and groups objects by type name. For each type, accumulates total count, total size, and per-generation counts (Gen0/1/2/LOH/POH). Two boolean flags are computed per MethodTable (cached to avoid repeating the check for every instance): `HasDispose` (type declares a `Dispose()` method) and `IsCritical` (type inherits from `CriticalFinalizerObject` or `SafeHandle` — determined by walking `BaseType` up to 20 steps). Both caches are keyed by `MetadataToken` to avoid per-object string comparisons.

**Phase 3 — Resurrection detection.** Builds a `HashSet<ulong>` of all strong GC handle target addresses, then cross-references the addresses of finalizable objects against this set. Any object that appears in both the finalizer queue and a strong GC handle is a resurrection candidate — its finalizer will keep it alive after finalization completes.

---

## Consumer: `FinalizerQueueConsumer`

**Class**: `DumpDetective.Analysis.Memory.Consumers.FinalizerQueueConsumer`  
**Implements**: `IFinalizableObjectConsumer` (not `IHeapObjectConsumer`)  
**Walk**: Sequential `heap.EnumerateFinalizableObjects()` pass — separate from the parallel heap walk  
**Output**: `FinalizerQueueData { Stats, Total, TotalSize }`, cached in session

### Why a different interface?

`EnumerateFinalizableObjects()` returns a small list (typically ≤ 10,000 objects), so parallelizing it adds overhead with no benefit. The `IFinalizableObjectConsumer` interface is a single-method sequential API: `ConsumeFinalizableObject(in ClrObject obj, ClrHeap heap)` plus `OnFinalizableQueueComplete()`.

### Per-object logic

For each valid finalizable object, the consumer reads the type name, object size, and generation index (0–4, where 3 = LOH and 4 = POH). Two per-MethodTable results are cached using `MetadataToken` as the key: `HasDispose` (checked once by scanning `obj.Type.Methods` for `"Dispose"`) and `IsCritical` (checked once by walking `BaseType` up to 20 steps looking for `CriticalFinalizerObject` or `SafeHandle`). With hundreds of instances of the same type in the queue, this per-MethodTable cache eliminates repeated LINQ scans over the method list and repeated base-type chain traversals.

All per-type accumulation uses `CollectionsMarshal.GetValueRefOrAddDefault` to update counters in-place with no per-object allocation.

`OnFinalizableQueueComplete()` converts the internal accumulator dictionary to the `FinalizerQueueData` result record.

---

## Disk cache

`.ddcache/<dumpName>/finalizer-queue.bin` — binary format with magic + version + dump identity + serialized `FinalizerQueueData`. Built by `load`. Subsequent runs on the same dump skip the queue scan.

---

## Options

| Option | Description |
|---|---|
| `--addresses` | Capture up to 5 sample heap addresses per type in the queue |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **`FinalizerThreadBlocked = true`** | The finalizer thread is stuck. All objects in the queue are waiting. Unmanaged resources (file handles, sockets, connections) are not being released. Cross-reference with `deadlock-detection`. |
| **Large queue depth (> 1,000 objects)** | Finalizable objects created faster than finalization can keep up, OR finalizer thread blocked. Monitor over time with `trend-analysis`. |
| **`IsCritical = true` types in queue** | `SafeHandle` or `CriticalFinalizerObject` subclasses — handles to native OS resources. If the queue backs up, OS handles are not being released, potentially exhausting OS limits. |
| **`HasDispose = true` types accumulating** | `IDisposable` objects not being disposed — relying on finalization. Wrap with `using` / `await using`. |
| **DB connection or stream types in queue** | Classic resource management failure — connections/streams opened without `using`. |
| **`ResurrectionCount > 0`** | A `Finalize` method is re-registering the object for finalization or storing it in a static. Intentional in some cache implementations but often a memory bug. |

---

## Typical output shape

```
Finalizer Queue — 2,441 objects  •  480 MB

Finalizer Thread: OS TID 4812  •  Managed TID 1  •  IDLE (not blocked)

Type                                     Count   Total Size  Critical?  Has Dispose?
System.Data.SqlClient.SqlConnection        882       88 MB     Yes         Yes
System.IO.FileStream                       441       44 MB     No          Yes
MyApp.Services.ResourceHolder              312       82 MB     No          Yes
System.Threading.Timer                     312        9 MB     No          Yes

Resurrection Candidates: 3
```
