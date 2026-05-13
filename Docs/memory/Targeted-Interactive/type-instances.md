# type-instances

**Category:** Targeted / Interactive  
**Included in `analyze --full`:** No (requires `--type` argument)

## What it does

Lists individual instances of types whose name matches a search substring, showing each instance's address, own size, generation, and (when BFS cache is available) retained size. Useful for deep-diving into a specific type found suspicious by `heap-stats` or `memory-leak`.

---

## Analyzer: `TypeInstancesAnalyzer`

**Class type:** Not `IHeapObjectConsumer` — always runs its own targeted heap walk  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

### Why no fast path

The pre-warmed `HeapSnapshot.TypeStats` stores only aggregate counts and sizes — not individual addresses. A heap walk is always required to find individual instances.

### How it works

Enumerates `ctx.Heap.EnumerateObjects()` with a case-insensitive substring filter on `obj.Type.Name`. Optional filters for minimum size (`--min-size`) and generation (`--gen`) are applied per object. Per-type statistics are updated using `CollectionsMarshal.GetValueRefOrAddDefault` — no per-object allocation. The top-N largest instances per type are tracked using a maintained-min heap, so only O(N) memory is used regardless of how many instances are found; the smallest entry is evicted when the heap exceeds `--top` capacity.

A spinner updates every ~200ms during the walk.

**Retained size** (`--retained` flag): after the walk, retained sizes for the top instances are computed. If `BfsIndexCache.TryLoad` succeeds (the BFS index was built by `load`), each lookup is O(1) — `cache.GetRetainedSize(addr)`. Without the cache, a BFS is run from each address, which may be slow for large instance sets.

---

## Disk cache

None for `TypeInstancesData`. Uses `BfsIndexCache` (`.ddcache/<dumpName>/<dumpName>.bfs.idx`) for retained sizes if `--retained` is specified.

---

## Options

| Option | Description |
|---|---|
| `--type <text>` | Type name substring to match (required) |
| `--top <n>` | Number of largest instances to display per type (default: 50) |
| `--min-size <bytes>` | Minimum instance size filter |
| `--gen <gen>` | Filter by generation: `Gen0`, `Gen1`, `Gen2`, `LOH`, `POH` |
| `--retained` | Compute retained size per instance (requires or builds BFS cache) |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **Instance count matches expected or much larger?** | If far larger than expected, objects are not being released. Run `gc-roots` on a sample address to find the retention path. |
| **All instances in Gen2** | Objects survived multiple GCs — long-lived or leaked. |
| **`MaxSingle` is very large** | One instance dominates size. Run `object-inspect` on that address to see what fields it contains. |
| **`RetainedSize` >> `Size` per instance** | This instance is the root of a large sub-graph — the leak is rooted here. Clear this reference to free the sub-graph. |
| **Many instances with identical size** | Possibly fixed-capacity collections all at the same capacity — check if over-allocated. |

---

## Typical output shape

```
Type Instances — MyApp.Models.Order  •  4,221,441 instances

  Total: 4,221,441 objects  •  840 MB own  •  Gen2: 100%

  Top 50 Largest Instances:
  Address              Size      Retained   Gen
  0xFFFF1234...        8.4 MB    12.1 MB    Gen2
  0xFFFE4421...        7.2 MB    10.8 MB    Gen2
  0xFFFC1100...        6.1 MB     9.2 MB    Gen2
```
