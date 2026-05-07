# gen-summary

**Category:** Heap / Memory  
**Included in `analyze --full`:** Yes

## What it does

Reports the size and object count of each GC generation and heap region — Gen0, Gen1, Gen2, LOH, POH, and Frozen — plus a per-segment breakdown. It gives the high-level view of where memory lives in the generational heap: how much is ephemeral (about to be collected), how much is long-lived (Gen2), and how much is in special regions like the Large Object Heap or the Pinned Object Heap.

Each row shows committed size (how much the GC has reserved from the OS for that region) and object count. The per-segment table — available with `--detail` — shows the base address of every GC segment so you can correlate with debugger output.

---

## Analyzer: `GenSummaryAnalyzer`

**Class type:** Not `IHeapObjectConsumer` — reads segment structures and `HeapSnapshot` counters  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

### How it works

**Segment scan** always runs and is always fast. It iterates `ctx.Heap.Segments` — a ClrMD in-memory list that requires no disk I/O — and reads the committed memory size of each segment from `seg.CommittedMemory.Length`. For ephemeral segments (which host Gen0, Gen1, and Gen2 side by side), the per-generation byte counts are computed from each segment's own `Generation0.Length`, `Generation1.Length`, and `Generation2.Length` range fields. Non-ephemeral segments (dedicated Gen2, LOH, POH, Frozen) map their full committed size to the corresponding generation.

**Object counts** come from `ctx.Snapshot` when available — specifically the counters accumulated by `GenCounterConsumer` during the main heap walk. Reading snapshot counters is O(1). When no snapshot exists, the analyzer falls back to a full `heap.EnumerateObjects()` walk, resolving each object's segment by address to assign it to a generation.

If `ctx.Heap.CanWalkHeap` is false (e.g. the CLR module was unloaded in the dump), all object counts are zeroed and a warning is shown.

---

## Consumer: `GenCounterConsumer`

**Class**: `DumpDetective.Analysis.Memory.Consumers.GenCounterConsumer`  
**Implements**: `IHeapObjectConsumer`  
**Walk**: `DumpCollector.CollectHeapObjectsCombined` — participates in every combined walk

### What it tracks

`GenCounterConsumer` maintains per-generation byte and object count accumulators for all five generation categories (Gen0, Gen1, Gen2, LOH, POH, Frozen). It also tracks a separate LOH-threshold counter — the count and total bytes of any object with a size ≥85,000 bytes regardless of which segment it lives in. This LOH-threshold counter is useful because some large objects land in Gen2 (e.g. arrays just under 85 KB) rather than the true LOH.

### Per-object logic

For each object, the consumer first checks if its size is ≥85,000 bytes and, if so, increments the LOH-threshold accumulators. It then resolves the object's segment via `heap.GetSegmentByAddress(obj.Address)` and switches on the segment kind. Ephemeral segments require the same address-range check as `TypeStatsConsumer`: Gen0 at highest addresses, Gen2 at lowest.

### Parallel clone and merge

`CreateClone()` returns a fresh empty consumer. `MergeFrom(clone)` sums all 13 numeric fields: six byte-total fields, six object-count fields, and the LOH-threshold pair. No locking is needed because each clone writes to its own fields in isolation.

---

## Pre-warm path

`GenCounterConsumer` runs during `DumpCollector.CollectHeapObjectsCombined`. Segment sizes are always read live from `ctx.Heap.Segments` (no cache needed — they're in-memory segment descriptors).

---

## Disk cache

None.

---

## Options

| Option | Description |
|---|---|
| `--detail` | Show per-segment table with addresses and committed sizes |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **Very large Gen2** | Long-lived objects dominating memory. Normal GC promotes objects through generations — if Gen2 is > 80% of heap, objects are not being released. Investigate with `memory-leak` and `heap-stats`. |
| **Large LOH (≥ 500 MB)** | Large buffer allocations (≥ 85 KB each). LOH is only collected during Gen2 GC (triggered infrequently) and is not compacted by default — leads to fragmentation. See `heap-fragmentation` and `large-objects`. |
| **Gen0 ≈ 0, Gen1 ≈ 0** | Dump taken right after a full (Gen2) GC — all ephemeral objects were either collected or promoted. Normal and expected. |
| **Many segments (> 10–20)** | Heap has grown significantly beyond its initial reservation — indicates sustained allocation pressure. Each segment represents a ~256 MB–4 GB committed region. |
| **POH large** | Heavy pinning — objects pinned via `GCHandle.Alloc(Pinned)` or `fixed` blocks prevent GC compaction. See `pinned-objects`. |
| **Frozen heap > 0** | Interned strings and frozen memory (System.Private.CoreLib frozen segments). Expected; should be < 100 MB in most apps. |

---

## Typical output shape

```
GC Generation Summary

Generation   Committed Size   Object Count   % of Heap
Gen0              128 MB        1,241,002       2%
Gen1              512 MB        8,890,341      10%
Gen2               10.2 GB     72,441,991      82%
LOH                 1.1 GB             —        9%
POH                48 MB              —        <1%
Frozen             12 MB              —        <1%
Total              12.0 GB

Segment Breakdown (18 segments)
  0x...  Ephemeral     640 MB
  0x...  Gen2           2.2 GB
  0x...  LOH            1.1 GB
  ...
```
