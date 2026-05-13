# memory-leak

**Category:** Heap / Memory  
**Included in `analyze --full`:** Yes

## What it does

`memory-leak` is the primary command for diagnosing memory pressure. It ranks all application types by instance count and retained size, identifies accumulation patterns (LOH buffer growth, over-sized collections, delegate leaks, string duplication), and traces exact GC root paths for the top suspects. The root chain answer — "what is keeping these objects alive?" — is the most valuable output.

The command is smart about avoiding redundant work. If `heap-stats` or the main pre-warm walk already ran, type statistics are read from the session cache in O(type count) time rather than re-walking the heap. Similarly, if `high-refs` ran first in the same session, its referrer map is reused for root-chain tracing; neither command pays the cost twice.

---

## Data collected

The result captures two ranked lists of suspects: one sorted by **instance count**, one by **total size**. System and BCL types are filtered out by default — the focus is on application-owned allocations. For each suspect type the report records generation distribution (what fraction of instances are in Gen2 vs. Gen0/1/LOH), an estimated or exact retained size, and up to three GC root chain samples.

Beyond the suspect lists, the command detects cross-cutting accumulation patterns:
- **String duplication** — total unique vs. duplicate strings, from `StringGroupConsumer` data
- **LOH byte arrays** — count and total bytes of `System.Byte[]` in the Large Object Heap
- **Collection bloat** — top oversized `List`, `Dictionary`, `ConcurrentBag` instances (reads `_size` / `Count` field)
- **Delegate accumulation** — event handler fields with large subscriber lists
- **Task backlog** — count of `Task` objects still in running or waiting state

For each top suspect, retained size is exact when a BFS index cache exists (built by `load`). Without the cache, `RetainedSize` is omitted — not an error, just absent.

---

## Analyzer: `MemoryLeakAnalyzer`

`MemoryLeakAnalyzer` is not an `IHeapObjectConsumer`. It is an orchestrator that pulls from multiple pre-built data sources and performs its own targeted BFS traversals on demand.

### How it works

**Type stats** come first. If `ctx.Snapshot.TypeStats` is populated (it always is after `load` or `analyze --full`), the analyzer reads from that dictionary in O(type count) time and converts entries to sorted rows. Without a snapshot it falls back to a fresh `heap.EnumerateObjects()` walk.

**Suspect selection** filters the full type list down to application-owned types with at least `--min-count` instances (default: 500). `IsSystemType()` returns true for names starting with `System.`, `Microsoft.`, or matching compiler-generated patterns like `<>c__DisplayClass` and `<>d__`. The top N (default: 30) by count and by size form the two suspect lists.

**Pattern detection** cross-references the suspect list against snapshot data. String duplication ratio comes from `ctx.Snapshot.StringGroups`. For collection types among the suspects, up to three sample instance addresses stored in `TypeAgg` are read with `obj.ReadField<int>("_size")` or `obj.ReadField<int>("Count")` to detect capacity overgrowth. Delegate accumulation is detected via `HeapTypeMeta.DelegateFields`.

**Retained sizes** are resolved by loading `BfsIndexCache` from the dump's `.ddcache` folder. Each suspect type's sample address is looked up directly — the BFS graph was pre-computed; no traversal happens here. If the cache file is absent, retained sizes are omitted.

**GC root chain tracing** runs last and is skipped entirely with `--no-root-trace`. The analyzer needs a child→parent referrer map. It checks the session cache first: if `HighRefsAnalyzer` already built a `SharedReferrerCache`, it is reused. Otherwise, `ReferrerConsumer` drives a dedicated secondary heap walk to build it now and stores the result for the session. For each suspect type's sample addresses, the analyzer BFS-walks up the referrer map (max depth 60) until reaching a GC root. Roots are identified by cross-referencing `ctx.Runtime.EnumerateHandles()`, per-thread `EnumerateStackRoots()`, and static field enumeration. Each root chain is formatted as a sequence of typed steps with the terminal root kind labelled: `Static field`, `Thread stack`, `GCHandle (Strong)`, `GCHandle (Pinned)`, or `Finalizer queue`. Up to three sample chains per type are reported.

---

## Consumer: `ReferrerConsumer`

**Class**: `DumpDetective.Analysis.Memory.Consumers.ReferrerConsumer`  
**Walk**: Secondary dedicated walk via `SharedReferrerCache.Build()` — not part of `CollectHeapObjectsCombined`. Triggered once per session on demand by whichever of `MemoryLeakAnalyzer` or `HighRefsAnalyzer` runs first.

### Why this consumer is special

A naïve child→parent map for a 50M-object heap would require ~10 GB of memory as a full `Dictionary<ulong, List<ulong>>`. `ReferrerConsumer` reduces peak usage to ~1.3 GB through two techniques:

**256-stripe locking** eliminates the need for per-thread clones. The parent map is partitioned into 256 independent buckets, indexed by `(childAddr >> 3) & 0xFF`. `HeapWalker` runs segments in parallel; each worker thread locks only its target stripe before writing. Because `IsThreadSafe = true`, the consumer is shared across all parallel workers without cloning.

**`ParentSlots` stores only one parent per child** — sufficient for a single root-chain trace upward. This converts per-child storage from a `List<ulong>` (24-byte heap allocation minimum) to an inline 8-byte struct. The 256 stripe dictionaries together peak at ~1.3 GB during the walk.

**Disk-backed flush**: when the walk completes, each stripe is written sequentially to `DiskBackedParentMap` on disk and immediately released from memory. By the time the last stripe is flushed, the full in-memory map is gone. Subsequent root-chain BFS lookups page in only the specific child entries they need — typically ~72 KB of data rather than gigabytes.

### What happens per object

For every object in the heap, `ReferrerConsumer` iterates its outgoing references via `obj.EnumerateReferenceAddresses(carefully: false)`. For each non-null, non-self reference, it locks the child's stripe and records the parent address. For the ~30 "hot" addresses (top inbound ref counts, seeded from `InboundRefConsumer` results), it also tallies which types are pointing at them — this powers the referencing-type breakdown in `high-refs`.

### Outputs

| Property | Type | Contents |
|---|---|---|
| `ParentMap` | `DiskBackedParentMap` | Child→parent edges for BFS root tracing (demand-paged from disk) |
| `HotTypes` | `Dictionary<ulong, Dictionary<string,int>>` | Per hot-address count of referencing types (≈30 entries) |

---

## Disk cache

Retained sizes require a **BFS index cache** built automatically during `load`. The cache lives at `.ddcache/<dumpName>/<dumpName>.bfs.idx`. When present, retained sizes are exact; when absent they are omitted from the report without error.

The child→parent map written by `ReferrerConsumer` lives at `.ddcache/<dumpName>/<dumpName>.parents.bin` and is reused within the session but not persisted across sessions.

---

## Options

| Option | Description |
|---|---|
| `--top <n>` | Number of suspect types to analyze (default: 30) |
| `--min-count <n>` | Minimum instance count to include (default: 500) |
| `--include-system` | Include BCL/system types in suspects |
| `--no-root-trace` | Skip GC root chain tracing — much faster, no path info |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **Application domain-model type at top of size suspects** | A specific entity is accumulating — check the root chain. It typically ends at a static cache, a session dictionary, or an event handler subscriber list. |
| **Root chain ends at a static field** | A static collection is the anchor. Use `static-refs` to enumerate all static roots with retained sizes and find the worst offenders across the codebase. |
| **Root chain ends at a thread stack** | Objects are live inside an active, possibly stuck, request or operation at dump time. Cross-reference with `thread-analysis` to see if any threads are blocked. |
| **Root chain ends at `GCHandle (Pinned)`** | An object is pinned by native code or an explicit `GCHandle.Alloc(Pinned)` call. The GC cannot compact around it, causing LOH-like fragmentation even in the regular heap. |
| **`RetainedSize` far exceeds `Size`** | Each instance is rooting a large subgraph. The type itself is small; the damage is in what it holds. Breaking one reference in the chain frees a disproportionate amount of memory. |
| **High LOH byte-array count** | Large buffers (≥ 85 KB each) are not returned to a pool. Run `large-objects` to inspect individual array sizes and `heap-fragmentation` to measure the resulting LOH hole density. |
| **Gen2 fraction close to 100%** | Objects are surviving all GC collections. Combined with a high count, this is the clearest signal of a retention leak rather than an allocation spike. |
| **Many distinct types in Gen2** | No single leak source — global caches growing, session state accumulating, or a single root anchoring many types. Use `gc-roots` to enumerate all GC-root-reachable types directly. |

---

## Typical output shape

```
Memory Leak Analysis — top 30 suspects (min 500 instances)

Type                               Count    Total Size   Gen2     LOH      Retained
MyApp.Models.OrderLine        18,881,002    1.4 GB     100%      0%       3.8 GB
MyApp.Models.Order             4,221,441      840 MB    100%      0%       5.2 GB
System.Byte[]                  9,112,334    2.8 GB      41%     59%       2.8 GB
System.String                 28,441,992    3.1 GB      82%      0%       3.1 GB

Root Chain Sample — MyApp.Models.Order @ 0xFFFF1234 (200 bytes)
  MyApp.Models.Order @ 0xFFFF1234
    → MyApp.Collections.OrderCache._store [Dictionary`2]
      → MyApp.Infrastructure.EntityCacheManager.s_localCache  ← Static field (ROOT)
```

---

## Options

| Option | Description |
|---|---|
| `--top <n>` | Number of suspect types to analyze (default: 30) |
| `--min-count <n>` | Minimum instance count to include (default: 500) |
| `--include-system` | Include BCL/system types in suspects |
| `--no-root-trace` | Skip GC root chain tracing — much faster, no path info |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **Application domain-model type at top of size suspects** | A specific entity is accumulating — check the root chain. It typically ends at a static cache, a session dictionary, or an event handler subscriber list. |
| **Root chain ends at a static field** | A static collection is the anchor. Use `static-refs` to enumerate all static roots with retained sizes and find the worst offenders across the codebase. |
| **Root chain ends at a thread stack** | Objects are live inside an active, possibly stuck, request or operation at dump time. Cross-reference with `thread-analysis` to see if any threads are blocked. |
| **Root chain ends at `GCHandle (Pinned)`** | An object is pinned by native code or an explicit `GCHandle.Alloc(Pinned)` call. The GC cannot compact around it, causing LOH-like fragmentation even in the regular heap. |
| **`RetainedSize` far exceeds `Size`** | Each instance is rooting a large subgraph. The type itself is small; the damage is in what it holds. Breaking one reference in the chain frees a disproportionate amount of memory. |
| **High LOH byte-array count** | Large buffers (≥ 85 KB each) are not returned to a pool. Run `large-objects` to inspect individual array sizes and `heap-fragmentation` to measure the resulting LOH hole density. |
| **Gen2 fraction close to 100%** | Objects are surviving all GC collections. Combined with a high count, this is the clearest signal of a retention leak rather than an allocation spike. |
| **Many distinct types in Gen2** | No single leak source — global caches growing, session state accumulating, or a single root anchoring many types. Use `gc-roots` to enumerate all GC-root-reachable types directly. |

---

## Typical output shape

```
Memory Leak Analysis — top 30 suspects (min 500 instances)

Type                               Count    Total Size   Gen2     LOH      Retained
MyApp.Models.OrderLine        18,881,002    1.4 GB     100%      0%       3.8 GB
MyApp.Models.Order             4,221,441      840 MB    100%      0%       5.2 GB
System.Byte[]                  9,112,334    2.8 GB      41%     59%       2.8 GB
System.String                 28,441,992    3.1 GB      82%      0%       3.1 GB

Root Chain Sample — MyApp.Models.Order @ 0xFFFF1234 (200 bytes)
  MyApp.Models.Order @ 0xFFFF1234
    → MyApp.Collections.OrderCache._store [Dictionary`2]
      → MyApp.Infrastructure.EntityCacheManager.s_localCache  ← Static field (ROOT)
```
