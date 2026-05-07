# async-stacks

**Category:** Threads / Concurrency  
**Included in `analyze --full`:** Yes

## What it does

Finds all heap-resident async state-machine objects — the compiler-generated classes for every `async`/`await` method — and reports their suspension state. The **backlog total** is the count of state machines currently suspended at an `await` point, directly measuring how many concurrent async operations are in flight at the moment of the dump. A high backlog with a saturated thread pool is the classic async starvation signature.

---

## Analyzer: `AsyncStacksAnalyzer`

**Implements:** `IHeapObjectConsumer`  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

### `HeapTypeMeta.AsyncMethod` detection

`HeapTypeMeta` sets `AsyncMethod` (a `string?`) when the CLR type name matches the compiler-generated state machine pattern — type names containing `<` and `>d__` (e.g., `MyClass.<GetOrdersAsync>d__5`). The outer method name is extracted by stripping the angle brackets and the `d__N` suffix. System and Microsoft namespaces are matched by the same pattern if they follow the convention. For non-state-machine types this field is null, and the consumer returns immediately with no work.

### How it works

For each heap object where `meta.AsyncMethod` is non-null, the analyzer reads the `<>1__state` field via `obj.Type.GetFieldByName("<>1__state")` and `field.Read<int>(obj, interior: false)`. The C# compiler uses a specific convention for this integer: `-2` means Initial (state machine allocated but `MoveNext` not yet called), `-1` means Completed (ran to completion, threw, or was cancelled), and any non-negative value means the state machine is Awaiting at the Nth `await` point in the method body (0-indexed). Any read exception produces the `Unknown` label.

Only `Awaiting` state machines increment `BacklogTotal`. After the walk, all entries are stored as `AsyncStacksData`.

---

## Consumer: `AsyncMethodConsumer`

**Class**: `DumpDetective.Analysis.Memory.Consumers.AsyncMethodConsumer`  
**Implements**: `IHeapObjectConsumer`  
**Walk**: `DumpCollector.CollectHeapObjectsCombined`  
**Output**: `AsyncStacksData { BacklogTotal, Entries }`, cached via `ctx.SetAnalysis<AsyncStacksData>()`

### How it works

For each object, the consumer checks `meta.AsyncMethod` — null for non-state-machine types, which are skipped immediately. For matching objects, it reads `<>1__state` via `obj.Type.GetFieldByName("<>1__state")` and `field.Read<int>(obj, interior: false)`. State `≥ 0` means the state machine is suspended at an await point: `BacklogTotal` is incremented and the method name is recorded in `MethodCounts` using `CollectionsMarshal.GetValueRefOrAddDefault`. States `-1` (Completed), `-2` (Initial), and any exception result (`Unknown`) are recorded but do not increment `BacklogTotal`.

Clone/merge: each clone gets its own `_entries` list and `MethodCounts` dictionary. `MergeFrom` sums `BacklogTotal`, merges `MethodCounts` entry-by-entry, and extends `_entries`.

---

## Pre-warm path

`AsyncMethodConsumer` (in `DumpDetective.Analysis.Memory.Consumers`) runs during `DumpCollector.CollectHeapObjectsCombined`. It populates session cache under key `AsyncStacksData`. When `AsyncStacksAnalyzer.Analyze` is called subsequently, `ctx.GetAnalysis<AsyncStacksData>()` returns the pre-warmed result — no heap walk needed.

---

## Disk cache

None. Results are session-scoped.

---

## Options

| Option | Description |
|---|---|
| `--filter <text>` | Substring match on `StateMachineEntry.Method` |
| `--min-count <n>` | Only show method groups with ≥ n awaiting instances |
| `--state <label>` | Filter to a specific state: `Awaiting`, `Completed`, `Initial` |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **BacklogTotal in thousands** | Many concurrent async operations suspended simultaneously. Normal under load, but extreme counts indicate slow or stuck I/O. Cross-reference with `thread-pool` — if pool is also saturated, the two are likely related. |
| **Same method dominating `Awaiting` count** | That method is the convergence point for all requests. Every request reaches that `await` and parks. Check what it's awaiting (DB query, HTTP call, semaphore). |
| **`System.Net.Http.HttpClient.SendAsync` Awaiting in bulk** | All threads are waiting on HTTP. Target endpoint is slow or rate-limiting. |
| **`System.Data.SqlClient.SqlCommand.ExecuteReaderAsync` bulk** | Database is the bottleneck — slow queries, connection pool exhaustion, or locks. |
| **Large `Completed` count** | Completed state machines not yet GC'd. Under memory pressure, GC may be falling behind. |
| **`Initial` state machines accumulating** | Tasks constructed but never awaited or started — object lifecycle bug. |
| **State machine in `Awaiting` with state ≥ 1000** | Deeply nested awaits compiled into a large state machine — unusual; may indicate a code generation anomaly. |

---

## Typical output shape

```
Async Stacks — 18,432 state machines  •  Backlog (Awaiting): 17,811

Method                                         State      Count
MyApp.DataService.GetOrdersAsync               Awaiting   12,441
MyApp.CacheService.LoadAsync                   Awaiting    3,892
System.Net.Http.HttpClient.SendAsync           Awaiting    1,198
MyApp.DataService.GetOrdersAsync               Completed     412
MyApp.OrderProcessor.ProcessBatchAsync         Awaiting      278
MyApp.NotificationService.SendAsync            Initial        28
```
