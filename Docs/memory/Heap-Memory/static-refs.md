# static-refs

**Category:** Heap / Memory  
**Included in `analyze --full`:** Yes

## What it does

Enumerates all non-system static object-reference fields across all loaded types and computes how much memory each one is keeping alive. A static field is a GC root — an object it holds will never be collected as long as the AppDomain is running. This command answers "which static caches and singletons are responsible for most of the retained memory?"

Each result row shows the declaring type, field name, type of the referenced object, whether the root is a known collection type, and its retained size — the total bytes freed if that one static reference were cleared.

---

## Analyzer: `StaticRefsAnalyzer`

**Class type:** Not `IHeapObjectConsumer` — enumerates CLR type metadata and runs BFS  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

### How it works

**Field enumeration** iterates every module loaded in the runtime via `ctx.Runtime.EnumerateModules()`, then every type in each module, then every static field of each type. System and BCL types are skipped with `DumpHelpers.IsSystemType()`. Only reference-type fields (objects, arrays, strings) are included — value-type statics hold no object references. For each non-null static field, the field value is read with `f.ReadObject(ctx.Runtime.AppDomains[0])` and a result entry is recorded. Modules that fail to enumerate (unloaded, dynamic) are counted as skipped and reported in the header.

**Retained-size computation** runs after field enumeration using BFS traversal from each field's root address:

When a `BfsIndexCache` is available (built by `build-bfs` or `load`), each root address is looked up directly in the pre-built BFS index — an O(1) operation that returns the exact exclusive retained byte count. No traversal happens at analysis time.

Without the cache, the analyzer builds an in-memory referrer graph on demand and runs BFS from each root with a configurable node cap. The cap defaults to 1% of total heap objects (clamped between 10,000 and 80% of total). When the cap is hit, `IsEstimated = true` and the retained size is a lower bound. Up to 8 BFS walks run in parallel. All fields belonging to the same declaring type share a single visited set — so if a static `Dictionary` and a static `List` both reference the same backing array, it is counted once.

Results are sorted by retained size descending.

---

## Pre-warm path

`ctx.GetAnalysis<StaticRefsData>()` — if already run in the session (e.g. by `analyze --full`), returns immediately. The field enumeration and BFS computation are not repeated.

---

## Disk cache

Uses `BfsIndexCache` at `.ddcache/<dumpName>/<dumpName>.bfs.idx` for exact retained sizes. Presence of this file switches the analyzer from sampling to exact mode. Built by `build-bfs` command or triggered by `load`.

---

## Options

| Option | Description |
|---|---|
| `--filter <text>` | Substring match on declaring type name or field name |
| `--exclude <type>` | Exclude a specific type from results (repeatable) |
| `--exact` | Force exact BFS (no node cap) even without disk cache |
| `--bfs-depth <n>` | Custom BFS node cap for sampling mode |
| `--top <n>` | Number of static roots to show (default: 50) |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **Single static field with > 10% of heap as `RetainedSize`** | One global root is the dominant memory holder. Clearing it would free that much memory. Usually a global cache or registry. |
| **Static `Dictionary<,>` or `List<>` with huge retained size** | Unbounded cache — check if it has an eviction policy. `ConcurrentDictionary`, `MemoryCache`, custom `Dictionary` fields are all captured here. |
| **`IsEstimated = true` on large entries** | BFS was capped — retained size is a lower bound. Run with `--exact` or build the BFS cache for precise figures. |
| **Delegate or `Action<>`/`Func<>` in static fields** | Static event subscriptions or strategy patterns holding closures. Cross-reference with `event-analysis`. |
| **`SkippedModuleCount > 0`** | Some modules had read errors — results are incomplete for those modules. |

---

## Typical output shape

```
Static References — 441 non-null static object fields  •  8.8 GB total retained

Declaring Type                            Field Name            Field Type         Retained   Est?
MyApp.Infrastructure.EntityCache          s_localCache          Dictionary`2        4.4 GB      No
MyApp.Services.SessionManager             _activeSessions       ConcurrentDict`2    1.2 GB      No
MyApp.Services.MetricsService             _metricsHistory       List`1               880 MB     No
System.Threading.Thread                   s_asyncLocalValueMap  Dictionary`2         240 MB     Yes
```
