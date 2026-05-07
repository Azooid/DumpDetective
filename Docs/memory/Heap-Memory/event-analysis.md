# event-analysis

**Category:** Heap / Memory  
**Included in `analyze --full`:** Yes

## What it does

Detects event handler leaks by finding delegate-typed instance fields with large subscriber counts. When an event publisher outlives its subscribers (a common pattern with singletons publishing to short-lived objects), those subscribers cannot be GC'd until they unsubscribe. The result is a growing list of delegate objects keeping entire object graphs alive.

For each suspicious event field, the report shows the publisher type, field name, total subscriber count, how many subscribers are lambdas (closures that capture variables), whether any subscriber is rooted by a static field, and an estimate of the bytes retained by the subscriber graph.

---

## Analyzer: `EventLeakAnalyzer`

**Implements:** `IHeapObjectConsumer` directly — the analyzer IS the consumer  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

### How it works

`EventLeakAnalyzer` implements `IHeapObjectConsumer` and runs directly in the shared heap walk — it does not delegate to a separate consumer class.

For each object, the analyzer checks `HeapTypeMeta.DelegateFields` — a cached list of reference-type fields whose declared CLR type is a delegate. This list is pre-computed by `HeapWalker.BuildMeta` per MethodTable; if the type has no delegate fields, the consumer returns immediately with no work done.

System types are skipped via `DumpHelpers.IsSystemType()`. Compiler-generated noise — closures (`<>c__DisplayClass`), async state machines (`<>d__`), and similar — are filtered by `EventFieldFilter.IsNoiseType()`. Each remaining field is checked by `EventFieldFilter.IsLikelyEventField()`, which matches naming patterns: fields starting with `On`, ending with `Changed`, `Handler`, or `Event`, and C# event backing fields.

For matching fields, the delegate value is read via `field.ReadObject(obj.Address, false)`. The subscriber count is determined by reading the `_invocationList` array from `MulticastDelegate` (giving the length of the subscriber chain) or returning 1 for a single-target delegate. Per-`(publisherType, fieldName)` accumulators are updated using `CollectionsMarshal.GetValueRefOrAddDefault` — no per-object heap allocation.

After the walk, results are sorted by total subscriber count descending.

---

## Consumer: `EventDetailConsumer`

**Class**: `DumpDetective.Analysis.Memory.Consumers.EventDetailConsumer`  
**Implements**: `IHeapObjectConsumer`  
**Walk**: Secondary walk used by `--detail` mode to collect full per-subscriber information  
**Note**: The main pre-warm walk uses `EventLeakAnalyzer` directly. `EventDetailConsumer` is only activated when per-subscriber detail is requested.

### Key design: shared Interlocked progress counters

All clones share one `long[2]` array — `_shared[0]` holds the publisher count and `_shared[1]` the subscriber count. Clones write to it via `Interlocked.Increment` / `Interlocked.Add`, so live progress can be read from outside the parallel walk without waiting for the merge phase.

For each qualifying publisher object, the consumer collects the full subscriber list (up to the detail limit) from the `_invocationList` chain, reading each subscriber's `_target` object type and method name. Lambda subscribers are identified by compiler-generated method names containing `<`. Each subscriber is checked against the static-roots set to determine if it is statically rooted.

In `MergeFrom`, the shared Interlocked counters are already correct and are not re-summed — only the raw subscriber lists and instance counts are merged from each clone.

---

## Pre-warm path

`EventLeakConsumer` (in `Consumers/`) runs during `DumpCollector.CollectHeapObjectsCombined`. Populates `EventAnalysisData` in session cache.

---

## Disk cache

`.ddcache/<dumpName>/event-analysis.bin` — binary cache built by `load`. Format: magic + version + dump identity header + serialized `EventAnalysisData` (count-prefixed records). Loaded by `EventAnalysisAnalyzer.Analyze` before any heap walk.

---

## Options

| Option | Description |
|---|---|
| `--top <n>` | Show top N leaking event fields (default: 50) |
| `--min <count>` | Only show groups with ≥ n total subscribers |
| `--detail` | Populate `AllSubs` — list every individual subscriber |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **Single event field with thousands of subscribers** | Publisher is a long-lived singleton (static service, event bus) and subscribers are added but never removed. Typical in event aggregators, message buses, UI notifiers. |
| **Many fields on the same publisher type** | The entire publisher class is leaking — audit all `+=` in the codebase for this type and ensure corresponding `-=` on disposal. |
| **`LambdaCount` high** | Lambda event handlers capture variables (closures). The captured objects (often request-scoped) are retained for as long as the subscription lives. |
| **`IsStaticPublisher = true` AND `HasStaticSubs = false`** | Static publisher keeping instance-scope subscribers alive — the most dangerous pattern. |
| **`DuplicateCount` > 0** | Same subscriber registered multiple times — double-subscription bug; event fires multiple times and subscriber count grows beyond expected. |

---

## Typical output shape

```
Event Handler Analysis — 14 suspicious event fields  •  42,882 publisher instances

Publisher Type                        Field              Subs      Lambdas  Static?   Retained
MyApp.Services.EventBus               OnOrderCreated     41,882     8,441     Yes     320 MB
MyApp.Services.EventBus               OnOrderCancelled   38,441     7,221     Yes     290 MB
MyApp.Core.ApplicationLifetime        ApplicationStopped 12,112         0     Yes      44 MB
MyApp.Infrastructure.Cache            OnExpiry            8,882     8,882     No       88 MB
```
