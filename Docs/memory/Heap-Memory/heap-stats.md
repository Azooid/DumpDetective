# heap-stats

**Category:** Heap / Memory  
**Included in `analyze --full`:** Yes

## What it does

Lists every live object type on the heap sorted by total size. This is the first command to run when investigating memory — it answers "what is taking up the most memory, and where is it in the generational heap?"

Each row in the report represents one CLR type and carries: total instance count, total size in bytes, the dominant generation label (`Gen0`, `Gen1`, `Gen2`, `LOH`, or `Mixed` when spread across multiple), the MethodTable address, and Gen2-specific count and size. A type is labelled with a single generation when more than 80% of its instances live there; otherwise it shows `Mixed`.

---

## Analyzer: `HeapStatsAnalyzer`

**Class type:** Not `IHeapObjectConsumer` — reads pre-built `HeapSnapshot.TypeStats`  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

### Fast path (always taken after `load` or `analyze --full`)

Reads `ctx.Snapshot.TypeStats` — a `Dictionary<ulong, TypeAgg>` keyed by MethodTable, populated by `TypeStatsConsumer` during the shared heap walk. Each `TypeAgg` entry holds per-generation count and size totals, the type name, and up to 5 sample object addresses for downstream BFS tracing. **Reading this dictionary is O(type count), not O(object count)** — it completes in milliseconds even on a 110M-object heap.

### Slow path (no snapshot, or `--gen` / `--filter` filter active)

Walks `heap.EnumerateObjects()` directly, guarding each object with `obj.IsValid && obj.Type != null`. Generation is determined by looking up the segment containing the object's address — each segment has a kind (`Generation0`, `Generation1`, `Generation2`, `Large`, `Pinned`, `Ephemeral`). Ephemeral segments are subdivided further: within a single ephemeral segment, Gen0 occupies the highest addresses, Gen1 the middle range, and Gen2 the oldest portion.

Using `--gen` or `--filter` always forces the slow path, because `TypeStats` stores aggregated totals across all generations and cannot be filtered post-hoc without re-walking.

---

## Consumer: `TypeStatsConsumer`

**Class**: `DumpDetective.Analysis.Memory.Consumers.TypeStatsConsumer`  
**Implements**: `IHeapObjectConsumer`  
**Walk**: `DumpCollector.CollectHeapObjectsCombined` — participates in every combined walk (`load`, `analyze --full`)

### Per-object logic

For each object, the consumer resolves which generation it belongs to by looking up its containing segment. Segment kinds (`Generation0` through `Large`, `Pinned`, `Ephemeral`) map directly to generation labels. Ephemeral segments require an extra address-range check since they host Gen0, Gen1, and Gen2 simultaneously — Gen0 at the highest addresses, Gen2 at the lowest. Unknown segment kinds default to Gen2.

The consumer then finds or creates a `TypeAgg` entry for the object's MethodTable using `CollectionsMarshal.GetValueRefOrAddDefault` — a single dictionary lookup that both creates and returns a mutable reference, avoiding a second lookup on the insert path. It increments the count, adds the object's size, and increments the appropriate per-generation counters. Up to 5 sample object addresses are stored per type for use by downstream BFS root-chain analysis.

### Parallel clone and merge

`HeapWalker` splits heap segments into 8 buckets and processes them in parallel. Because `TypeStatsConsumer` is not thread-safe (no locking), `CreateClone()` returns a fresh empty consumer for each bucket. After all parallel walks complete, the master consumer calls `MergeFrom(clone)` for each of the 7 worker clones. Merge folds all count and size fields together and appends sample addresses up to the 5-address cap. Type entries that exist only in a clone are inserted directly into the master dictionary — no second allocation.

---

## Pre-warm path

`TypeStatsConsumer` runs during `DumpCollector.CollectHeapObjectsCombined`. Populates `ctx.Snapshot.TypeStats`. `HeapStatsAnalyzer` reads this snapshot — no secondary walk.

---

## Disk cache

None. Data comes from the in-memory `HeapSnapshot`.

---

## Options

| Option | Description |
|---|---|
| `--filter <text>` | Show only types whose name contains this string |
| `--gen <gen>` | Filter by generation: `Gen0`, `Gen1`, `Gen2`, `LOH`, `POH` |
| `--top <n>` | Number of types to show (default: 50) |
| `--sort size\|count` | Sort by total size (default) or instance count |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **One or two types dominating heap size** | Potential leak — the type's objects are not being released. Cross-reference with `memory-leak` for root chain analysis. |
| **`System.String` at top** | Common in all apps; run `string-duplicates` to measure deduplication potential. If strings are in Gen2 in large quantities, they're long-lived. |
| **`System.Byte[]` / `System.Char[]` large total** | Buffer accumulation — I/O buffers, response bodies not disposed, `MemoryStream` left open. LOH byte arrays (≥ 85 KB each) indicate LOH fragmentation. |
| **Application domain-model types in high Gen2 count** | App objects surviving into Gen2 — not being released after use. Cache growing unbounded, session state leaking, request objects retained by subscribers. |
| **Compiler-generated types (`<>c__DisplayClass`, `<>d__`)** | Closures and async state machines — see `async-stacks` for backlog and `memory-leak` for root chains. |
| **`System.WeakReference`** | Check with `weak-refs` — some may have null targets (effectively garbage). |
| **`System.Runtime.CompilerServices.StrongBox`** | Value types boxed and held by closures — often accumulates in high-throughput async paths. |

---

## Typical output shape

```
Heap Statistics — 110,472,530 objects  •  12.4 GB total

Type                                       Count        Size       Gen       Gen2 Size
System.String                         28,441,992    3.1 GB      Mixed       2.5 GB
System.Byte[]                          9,112,334    2.8 GB      LOH/Gen2    0.8 GB
MyApp.Models.OrderLine                18,881,002    1.4 GB      Gen2        1.4 GB
MyApp.Models.Order                     4,221,441      840 MB     Gen2        840 MB
System.Collections.Generic.List<>      3,112,009      480 MB     Gen2        480 MB
```
