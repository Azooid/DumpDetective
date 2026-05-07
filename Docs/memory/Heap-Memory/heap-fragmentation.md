# heap-fragmentation

**Category:** Heap / Memory  
**Included in `analyze --full`:** Yes

## What it does

Measures managed heap fragmentation across all GC segments — how much committed memory in each segment is occupied by live objects vs. unused holes left by freed objects. High fragmentation means the GC is holding committed memory that cannot be returned to the OS and cannot be compacted (especially in the LOH, which is not compacted by default).

The report shows per-segment live/free breakdowns and a histogram of free-hole sizes. Small holes (< 1 KB) indicate alignment padding and minor object churn; large holes (> 1 MB) indicate that significant buffers were freed without the GC being able to compact the space. Pinned object counts per segment are also reported — pinned objects are anchors that prevent the GC from sliding other objects to fill gaps.

---

## Analyzer: `HeapFragmentationAnalyzer`

**Implements:** `IHeapObjectConsumer` (via `FragmentationConsumer`)  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

Before starting the heap walk, the analyzer enumerates `ctx.Runtime.EnumerateHandles()` to count pinned handles per segment. This resolves each pinned object's segment via `ctx.Heap.GetSegmentByAddress()` and builds a per-segment pinned-handle count. These counts are attached to the final `HeapSegmentInfo` result for each segment.

The heap walk then proceeds via `FragmentationConsumer`.

## Consumer: `FragmentationConsumer`

**Class**: `DumpDetective.Analysis.Memory.Consumers.FragmentationConsumer`  
**Implements**: `IHeapObjectConsumer`  
**Walk**: `DumpCollector.CollectHeapObjectsCombined` or standalone for the `heap-fragmentation` command

### Per-object logic

For each object, the consumer looks up its segment and determines whether it is a live object or a GC free hole. Free holes are identified by reference-equality against `heap.FreeType` — a single pointer comparison, not a string match, making it allocation-free in the hot path.

Live objects increment the segment's `LiveBytes` counter. Free holes increment `FreeBytes` and also land in one of six logarithmic size buckets:

| Bucket | Size range | What it usually means |
|---|---|---|
| 0 | < 128 bytes | Alignment gaps between small objects |
| 1 | 128 bytes – 1 KB | Freed small objects, struct arrays |
| 2 | 1 KB – 4 KB | Small string and byte-array allocations |
| 3 | 4 KB – 64 KB | Medium buffers, small request objects |
| 4 | 64 KB – 1 MB | Near-LOH-threshold buffers freed in Gen2 |
| 5 | ≥ 1 MB | Large buffers: `MemoryStream`, big `byte[]` |

The bucket histogram is accumulated per-stripe and merged using `CollectionsMarshal.GetValueRefOrAddDefault` — no per-entry allocation.

### Clone and merge

`CreateClone()` returns a new consumer pre-seeded with the segment structure (same `Kind`, address, and committed size) but with zeroed live/free counters. After the parallel walk, `MergeFrom(clone)` sums `LiveBytes` and `FreeBytes` per segment address and folds the bucket histograms.

---

## Disk cache

`.ddcache/<dumpName>/heap-fragmentation.bin` — binary format with magic + version + dump identity + serialized `HeapFragmentationData`. Validated by dump length + ticks before loading. Built by `load` and saved automatically. Subsequent runs on the same dump skip the heap walk entirely.

---

## Options

| Option | Description |
|---|---|
| `--detail` | Show per-segment table with address, kind, live/free breakdown, pinned count |
| `--min-frag <pct>` | Only show segments with fragmentation % ≥ this value (0–100) |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **LOH fragmentation > 30%** | LOH is not compacted by default. Many large allocations and releases left permanent holes. Fix: use `ArrayPool<byte>`, size arrays just under 85 KB, or set `GCLargeObjectHeapCompactionMode.CompactOnce` before GC. |
| **Gen2 fragmentation > 20%** | Long-lived objects freed in Gen2 left holes. Usually caused by pinned objects or alternating large/small allocations in Gen2. |
| **Many small free holes (< 1 KB) in LOH** | Medium-frequency large-object allocation pattern — many moderate-size buffers being freed. Cannot be compacted without explicit setting. |
| **Large free holes (> 1 MB)** | One or a few very large objects were freed — LOH has entire megabyte-scale gaps. A single `GC.Collect()` with compact mode would recover this. |
| **High `PinnedCount` in Gen0/Gen1** | Pinned objects in the young generation prevent GC from compacting ephemeral segments. Reduce pinning duration — avoid `GCHandle.Alloc(Pinned)` for long durations. |
| **Ephemeral segment fragmentation > 10%** | Dump taken mid-GC or under extreme allocation pressure. Check async-stacks for backlog. |

---

## Typical output shape

```
Heap Fragmentation — 18 segments

Generation   Committed   Live       Free      Frag %   Pinned
Gen0          128 MB    120 MB       8 MB       6%         0
Gen1          512 MB    490 MB      22 MB       4%         0
Gen2           10.2 GB   9.1 GB     1.1 GB     11%       412
LOH             1.1 GB 780 MB     320 MB       29%         0
POH            48 MB    46 MB       2 MB        4%    12,441

Free Hole Size Distribution (across all segments)
  < 1 KB           9,441 holes     3.2 MB
  1–16 KB          2,112 holes    14.1 MB
  16–256 KB          441 holes    32.8 MB
  256 KB–1 MB         88 holes    44.0 MB
  > 1 MB              12 holes   226.0 MB
```
