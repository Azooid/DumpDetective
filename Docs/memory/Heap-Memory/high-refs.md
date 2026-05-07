# high-refs

**Category:** Heap / Memory  
**Included in `analyze --full`:** Yes

## What it does

Finds the most-referenced ("hot") objects on the heap — objects pointed to by the largest number of other objects. Reveals shared roots like dictionaries, caches, string constants, and singleton services that anchor thousands or millions of child objects. A high inbound-reference count combined with a high retained size is a strong memory leak signal: something is holding a reference to this object, and through it to everything below it in the object graph.

Each result row shows the object's address, type, own size, retained size, dominant generation, inbound reference count, how many distinct source types hold references to it, and the top 5 referencing types by count.

---

## Analyzer: `HighRefsAnalyzer`

**Class type:** Not `IHeapObjectConsumer` — reads `HeapSnapshot.InboundCounts`; uses shared referrer cache  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

### How it works

**Inbound-ref counts** come from `ctx.Snapshot.InboundCounts` — a `Dictionary<ulong, int>` built by `InboundRefConsumer` during the main heap walk. For every object, `InboundRefConsumer` enumerated all outgoing reference fields and incremented `InboundCounts[childAddr]`. Reading this dictionary is O(type count), not O(object count). Without a snapshot, the analyzer falls back to a fresh `heap.EnumerateObjects()` walk.

**Top-N selection** sorts the inbound-counts dictionary by value descending, takes the top N (default: 30, configurable via `--top`), and optionally applies a `--min-refs` floor. Each selected address is resolved to a live `ClrObject` to confirm it is still valid.

**Source-type breakdown** uses the `SharedReferrerCache` — a child→parent map shared between `HighRefsAnalyzer` and `MemoryLeakAnalyzer`. Whichever analyzer runs first in a session builds it via a dedicated secondary heap walk; the other gets it instantly from the session cache. For each hot address, the analyzer looks up all parent addresses in the map and counts them by type name to produce `TopSources` and `DistinctSourceTypes`.

**Retained sizes** are exact when a `BfsIndexCache` is available (built by `build-bfs` or `load`). Each candidate's address is looked up in the pre-computed BFS index for its exclusive retained byte count. Without the cache, the analyzer approximates retained size by summing the direct children's sizes, capped at 2,000 children to avoid runaway on huge arrays.

**Reference histogram** is built from the full `InboundCounts` dictionary in a single pass, bucketing all objects into ranges: 1–10, 11–100, 101–1K, 1K–10K, 10K+.

---

## Pre-warm path

`ctx.GetAnalysis<HighRefsData>()` — if already run, returns immediately. `InboundCounts` comes from `ctx.Snapshot` (populated by `InboundRefConsumer`). `SharedReferrerCache` is shared with `MemoryLeakAnalyzer`.

---

## Consumer: `InboundRefConsumer`

**Class**: `DumpDetective.Analysis.Memory.Consumers.InboundRefConsumer`  
**Implements**: `IHeapObjectConsumer`; `IsThreadSafe = true` (no cloning)  
**Walk**: `DumpCollector.CollectHeapObjectsCombined`

### Key design: 256-stripe locking

Building a `Dictionary<ulong, int>` for all reference edges in a 50M-object heap without cloning would create write contention across parallel workers. `InboundRefConsumer` solves this with 256 independent stripe dictionaries, each protected by its own lock. The target stripe for a child address is `(childAddr >> 3) & 0xFF` — right-shifting by 3 avoids the clustering that would occur from the lower 3 bits always being zero on 8-byte-aligned heap objects.

With `IsThreadSafe = true`, `HeapWalker` does not clone the consumer — all 8 parallel workers share the same instance and lock only the single stripe they need to write. Contention is minimal: each stripe handles only 1/256th of all addresses.

### Per-object logic

For every live object, the consumer iterates its outgoing reference fields via `obj.EnumerateReferenceAddresses(carefully: false)`. For each non-null child address, it locks the child's stripe and increments its inbound count using `CollectionsMarshal.GetValueRefOrAddDefault` — a no-alloc increment that avoids a double-lookup on the hot add path. Corrupted or unreadable references are caught silently and skipped.

### Memory-efficient post-processing in `OnWalkComplete()`

Rather than merging all 256 stripes into one large dictionary (which would briefly double memory usage to ~5 GB), the consumer iterates each stripe once, collecting only entries with a count of 10 or more into a compact result list. Each stripe dictionary is cleared and nulled immediately after being read, releasing its memory before moving to the next stripe. The walk's ~2.5 GB of stripe memory drops to near zero as stripes are freed one by one.

### Outputs

| Property | Type | Contents |
|---|---|---|
| `TopAddrs` | `HeapAddrCount[]` | Objects with ≥10 inbound refs, sorted descending |
| `Histogram` | `InboundBucket[]` | Distribution across buckets: 10–49, 50–99, 100–499, 500–999, 1K–9K, 10K+ |
| `TotalRefs` | `long` | Total reference edges counted across all objects |
| `InboundCountsSize` | `int` | Total distinct addressed referenced by at least one other object |

---

## Options

| Option | Description |
|---|---|
| `--top <n>` | Number of hot objects to show (default: 30) |
| `--min-refs <n>` | Minimum inbound reference count (default: 10) |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **Object with millions of inbound refs** | A shared singleton referenced from almost every domain object. Confirm type is expected — e.g. a shared string constant or config object. |
| **Application cache/dictionary with high refs AND high retained size** | Removing or capping this object would free significant memory. The root of many object chains. |
| **`DistinctSourceTypes` = 1** | Only one type holds references — makes the retention path easy to identify and fix. |
| **`DistinctSourceTypes` very high** | Referenced from many different types — a shared infrastructure object (logger, config, DI container). Usually expected. |
| **`RetainedSize` >> `OwnSize`** | This object is the root of a large sub-graph. The object itself is small but keeps gigabytes alive. |
| **`InboundRefs` growing across dumps** | Combined with `trend-analysis` — the reference count is growing, meaning more and more objects are referencing it. |

---

## Typical output shape

```
High-Reference Objects — top 30 (min 10 refs)

Address           Type                                    Inbound Refs   Retained Size   Distinct Sources
0xFFFF1234    MyApp.Infrastructure.EntityCacheManager     18,441,882      4.4 GB              2
0xFFFE4421    System.String ("application/json")             441,882        77 MB              12
0xFFFC1100    MyApp.Config.AppConfiguration                  312,441       120 MB              8
0xFFFB2200    System.Collections.Generic.Dictionary`2         88,441       280 MB              1
```
