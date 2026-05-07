# pinned-objects

**Category:** Heap / Memory  
**Included in `analyze --full`:** Yes

## What it does

Lists every GC-pinned object in the process — objects the garbage collector cannot move during compaction. Pinned objects anchor heap memory: by preventing the GC from sliding adjacent live objects together, they create permanent holes (fragmentation) that grow over time without becoming reclaimable.

The report distinguishes two pin sources: **explicit pins** created by user code via `GCHandle.Alloc(obj, GCHandleType.Pinned)`, and **async I/O pins** created by the runtime for overlapped socket and file operations. The two have very different lifetimes and implications.

---

## Analyzer: `PinnedObjectsAnalyzer`

**Class type:** Not `IHeapObjectConsumer` — reads the GC handle table directly  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

### How it works

Enumerates `ctx.Runtime.EnumerateHandles()` and filters for handles where `h.IsPinned == true` and `h.Object != 0`. Two handle kinds are captured: `ClrHandleKind.Pinned` (explicit user pins) and `ClrHandleKind.AsyncPinned` (runtime-managed overlapped I/O pins). Async pins are flagged in the output as `Async = true`.

For each pinned handle, the analyzer resolves the target object via `ctx.Heap.GetObject(h.Object)` to read its type name and size. The generation is determined from the segment kind containing the object's address. Entries are grouped by `(TypeName, IsAsync)` for the summary table, with individual entries available in `--detail` mode.

---

## Disk cache

None. Reads directly from the GC handle table in the dump.

---

## Options

| Option | Description |
|---|---|
| `--filter <text>` | Substring match on type name |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **Large count of explicit Pinned handles** | `GCHandle.Alloc(Pinned)` without corresponding `Free()`. Common in unsafe interop or old-style P/Invoke patterns. Each pin prevents compaction of the containing segment. |
| **Many AsyncPinned handles** | Many concurrent overlapped I/O operations in flight (socket receives, file reads). Expected in high-throughput network code. Extremely high counts (> 100K) suggest an I/O backlog — correlate with `async-stacks`. |
| **Pinned objects in Gen2** | Long-lived pinned buffers in the old generation. Gen2 is compacted rarely (only during full GC), so Gen2 pins cause long-term fragmentation. |
| **`System.Byte[]` dominating** | Standard pattern for socket/file buffer pinning. Use `System.IO.Pipelines` or `MemoryPool<byte>` to reduce total number of simultaneous pins. |
| **Many small pinned arrays (< 1 KB)** | Scattered small pins fragment multiple GC segments simultaneously. Consolidate into fewer larger buffers. |
| **Total pinned size > 5% of heap** | Unusual — investigate which subsystem holds these pins and whether durations can be shortened. |

---

## Typical output shape

```
Pinned Objects — 14,882 pinned handles

Type                                    Count    Total Size   Gen    Async?
System.Byte[]                          12,441      890 MB    Gen2     Yes
System.Byte[]                           1,882      240 MB    Gen0     Yes
System.Runtime.InteropServices.GCHandle    441       18 MB    Gen2      No
MyApp.Interop.NativeBuffer                118        9 MB    LOH       No
```
