# DumpDetective Cache Inventory

This document lists built-in system caches used by DumpDetective.

Scope:
- Includes host/runtime/analysis/report caches.
- Excludes plugin-owned custom caches.

---

## Cache Layers

| Layer | Lifetime | Stored where | Purpose |
|---|---|---|---|
| Session cache (`DumpContext`) | Single command/process run | Memory | Share expensive computed data across parallel analyzers in one run |
| Snapshot cache (`HeapSnapshot`) | Single command/process run | Memory + `.ddcache` files | Reuse one heap walk output across many analyzers |
| Disk analysis caches (`.ddcache`) | Across runs | `<dump-dir>/.ddcache/<dump-name>/` | Avoid rebuilding expensive structures |
| Trace conversion cache (`.etlx`) | Across runs | Next to `.etl` | Reuse parsed trace database |

---

## 1) Session In-Memory Caches (`DumpContext`)

Defined in `DumpDetective.Core/Runtime/DumpContext.cs`.

| Cache | API | Key | Notes |
|---|---|---|---|
| Analysis object cache | `GetAnalysis<T>()`, `SetAnalysis<T>()` | `typeof(T)` | Simple typed lookup for pre-populated analysis artifacts |
| Once-cache | `GetOrCreateAnalysis<T>(factory)`, `PreloadAnalysis<T>()`, `ReplaceAnalysis<T>()` | `typeof(T)` | Thread-safe lazy compute-once cache for parallel full-analyze workers |
| Pin registry | `PinAnalysis(Type)`, `UnpinAnalysis(Type)`, `IsAnalysisPinned(Type)` | `Type` | Prevents early replacement/release while dependent commands still run |

### Pin behavior (current)

`ICommandCachePin` is dependency-aware:
- Types are pinned before parallel sub-reports start.
- Reference counts are tracked per declared dependent command.
- A type is unpinned immediately after the last dependent command completes.
- Not tied to command index order; tied to completion order.

---

## 2) Shared Heap Snapshot Cache (`HeapSnapshot`)

Defined in `DumpDetective.Core/Runtime/HeapSnapshot.cs`.

Built from a single combined heap walk and reused by many analyzers.

| Data | Storage | Access pattern |
|---|---|---|
| `TypeStats` | Memory (`Dictionary<string, TypeAgg>`) | Read by multiple analyzers; released by reader retirement |
| Inbound summaries (`TopInboundAddrs`, histogram) | Memory | Lightweight distilled data kept for reporting |
| `StringGroups` | Disk (`stringGroups.bin`) | Streamed on demand; avoids keeping huge string dictionaries in memory |
| Generation/object totals | Memory scalars | Used by scoring and summary renderers |

Important:
- Raw inbound dictionary is intentionally not retained inside `HeapSnapshot`; it is reduced and released early.

---

## 3) Persistent Dump Caches (`.ddcache/<dump-name>/`)

Primarily built by `load` and reused by `analyze --full`.

| File | Producer | Main consumers |
|---|---|---|
| `stringGroups.bin` | `HeapSnapshot.Create(...)` | `string-duplicates` |
| `fragmentation.bin` | `FragmentationCache` | `heap-fragmentation` |
| `gc-roots.bin` | `GcRootsCache` | `gc-roots`, leak/root analyzers |
| `static-roots.bin` | `StaticRootsCache` | `static-refs`, event/root diagnostics |
| `hot-addr-types.bin` | `HotAddrTypesCache` | `high-refs`, retained/referrer diagnostics |
| `finalizer-queue.bin` | `FinalizerQueueCache` | `finalizer-queue` |
| `event-analysis.bin` | `EventAnalysisCache` | `event-analysis` |
| `<dump>.bfs.idx` | `BfsIndexCache` | retained-size / BFS-based analyzers |
| `<dump>.parent.map` | `DiskBackedParentMap` / `SharedReferrerCache` | root-chain / referrer traversals |

Related helper entry points:
- `DumpDetective.Analysis.Memory.LoadHelper.BuildReferrerCache(...)`
- `DumpDetective.Analysis.Memory.LoadHelper.BuildStaticRootsCache(...)`

---

## 4) Trace Cache (`.etlx`)

For `.etl` inputs, DumpDetective uses TraceEvent conversion/open logic that produces/reuses `.etlx` databases.

| File | Producer | Consumer |
|---|---|---|
| `<trace>.etlx` | Trace open/convert pipeline | `trace-analyze`, `trace-dump-analyze`, standalone trace analyzers |

`close` can remove these when run against trace targets.

---

## Invalidation and Cleanup

| Action | Effect |
|---|---|
| `load --force` | Rebuilds all dump caches even if valid |
| Version/timestamp checks (`IsValid`) | Rejects stale or mismatched cache files |
| `close <dump-or-dir>` | Deletes `.ddcache/<name>/` and related cache artifacts |
| Session end (`DumpContext.Dispose`) | Releases in-memory references |

---

## GetAnalysis Type Catalog (Built-in)

The table below lists built-in cache entry types that are stored in `DumpContext` and can be read via `GetAnalysis<T>()`.

### A) Pre-populated during shared full heap walk

These are set in `CollectHeapObjectsCombined(...)` before parallel sub-reports run.

| Type key (`T`) | Produced by | Common consumers |
|---|---|---|
| `TimerLeaksData` | `TimerLeaksAnalyzer` (consumer mode) | `timer-leaks` |
| `WcfChannelsData` | `WcfChannelsAnalyzer` (consumer mode) | `wcf-channels` |
| `ConnectionPoolData` | `ConnectionPoolAnalyzer` (consumer mode) | `connection-pool` |
| `ExceptionAnalysisData` | `ExceptionAnalysisAnalyzer` (consumer mode) | `exception-analysis` |
| `AsyncStacksData` | `AsyncStacksAnalyzer` (consumer mode) | `async-stacks`, `http-requests` correlation hints |
| `HttpRequestsData` | `HttpRequestsConsumer` | `http-requests` |
| `CwtData` | `ConditionalWeakTableConsumer` | `weak-refs` |
| `ThreadPoolConsumerCache` | `ThreadPoolConsumer` | `thread-pool` |
| `DataTableConsumerResult` | `DataTableConsumer` | `datatable-amp` |
| `CachePatternsConsumerResult` | `CachePatternsConsumer` | `cache-patterns` |
| `ClosureCaptureConsumerResult` | `ClosureCaptureConsumer` | `closure-capture` |
| `AlcConsumerResult` | `AlcConsumer` | `module-list` |
| `LargeObjectsConsumerResult` | `LargeObjectsConsumer` | `large-objects` (default threshold path) |

### B) Materialized on demand, then cached via `SetAnalysis`

These may not exist at the start of a run, but once built/loaded they are stored in `DumpContext` and become available via `GetAnalysis<T>()`.

| Type key (`T`) | Produced by | Common consumers |
|---|---|---|
| `EventAnalysisData` | `EventAnalysisAnalyzer` (disk load or full scan) | `event-analysis` |
| `FinalizerQueueData` | `FinalizerQueueAnalyzer` (disk load or queue scan) | `finalizer-queue` |
| `StaticRootAddresses` | `EventAnalysisAnalyzer` / static-root builder | `event-analysis`, root diagnostics |
| `ConfigSmellData` | `ConfigurationSmellAnalyzer` | `diagnostic-summary` |
| `WorkloadProfileData` | `WorkloadProfileClassifier` | `diagnostic-summary` |

### C) Related typed caches not read through `GetAnalysis<T>()`

These are still part of the built-in cache system, but accessed via the thread-safe once-cache API (`GetOrCreateAnalysis<T>()` / `PreloadAnalysis<T>()`).

| Type key (`T`) | Access path | Typical use |
|---|---|---|
| `ThreadNameMap` | `PreloadAnalysis` + `GetOrCreateAnalysis` | `thread-analysis`, `deadlock-detection` |
| `BfsCacheBox` | `PreloadAnalysis` + `GetOrCreateAnalysis` | BFS retained-size analyzers (`memory-leak`, `static-refs`, etc.) |
| `SharedReferrerCache` | `GetOrCreateAnalysis` | referrer/parent-map dependent analyzers |

Note:
- `GetAnalysis<T>()` reads only the analysis object cache.
- Once-cache entries are separate and are not returned by `GetAnalysis<T>()` unless explicitly also stored through `SetAnalysis<T>()`.

---

## What Is Not Included Here

Plugin-owned caches (custom `GetOrCreateAnalysis<T>()` types in plugin assemblies) are intentionally excluded from this inventory. They are plugin-defined, not host-defined system caches.
