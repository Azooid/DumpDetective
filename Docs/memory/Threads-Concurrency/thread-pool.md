# thread-pool

**Category:** Threads / Concurrency  
**Included in `analyze --full`:** Yes

## What it does

Reports thread pool health by showing worker thread counts (min, max, active, idle), `Task` distribution by state, and queued work-item type counts. Diagnoses thread pool starvation, async backlogs, and misuse of the pool.

---

## Analyzer: `ThreadPoolAnalyzer`

**Class type:** Not `IHeapObjectConsumer` — reads runtime thread-pool structures; Task counts from pre-warm cache  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

### How it works

Reads thread pool counters directly from `ctx.Runtime.ThreadPool`: `MinThreads`, `MaxThreads`, `ActiveWorkerThreads`, `IdleWorkerThreads`. If the property returns null (dump has no CLR runtime info), `InfoAvailable` is set to false and all counters are null.

Task state counts and work-item counts are read from the pre-warmed `ThreadPoolConsumerCache` built by `ThreadPoolConsumer` during the combined heap walk. If not pre-warmed (e.g. standalone command), a fresh `ThreadPoolConsumer` walk is triggered.

---

## Pre-warm path

`ThreadPoolConsumer` runs during `DumpCollector.CollectHeapObjectsCombined`. Populates `ThreadPoolConsumerCache` in session. `ThreadPoolAnalyzer` reads that cache directly via `ctx.GetAnalysis<ThreadPoolConsumerCache>()` — no second heap walk.

---

## Consumer: `ThreadPoolConsumer`

**Class**: `DumpDetective.Analysis.Memory.Consumers.ThreadPoolConsumer`  
**Implements**: `IHeapObjectConsumer`  
**Walk**: `DumpCollector.CollectHeapObjectsCombined`  
**Output**: `ThreadPoolConsumerCache { TaskCounts, WorkItems }`, cached via `ctx.SetAnalysis<ThreadPoolConsumerCache>()`

### Task state flag bitmasks

The C# `Task` class stores its state in the `m_stateFlags` int field. The consumer reads this field via `obj.ReadField<int>("m_stateFlags")` and applies the following bitmasks in priority order to produce a label:

| Label | Bitmask |
|---|---|
| `Faulted` | `0x0200000` |
| `Canceled` | `0x0400000` |
| `RanToCompletion` | `0x1000000` |
| `Running` | `0x0080000` (`TASK_STATE_DELEGATE_INVOKED`) |
| `WaitingToRun` | `0x0010000` (`TASK_STATE_STARTED`) |
| `WaitingForActivation` | `0x0001000` |
| `Other` | (no flags match) |

### How it works

For each heap object, `HeapTypeMeta.IsTask` is checked (set when the type name starts with `System.Threading.Tasks.Task`). For matching objects, `m_stateFlags` is read and the bitmask table applied. The resulting label is used as a key in `TaskCounts`, updated via `CollectionsMarshal.GetValueRefOrAddDefault`.

`HeapTypeMeta.IsWorkItem` is set for types whose name contains `ThreadPoolWorkItem` or implements `IThreadPoolWorkItem`. For these objects, the type name is counted in `WorkItems`.

Clone/merge: each clone maintains its own `TaskCounts` and `WorkItems` dictionaries. `MergeFrom` sums all entries using `GetValueRefOrAddDefault`.

---

## Options

| Option | Description |
|---|---|
| `--tasks` | Show full Task state breakdown table |
| `--work-items` | Show work-item type counts |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **`ActiveWorkers == MaxThreads`** | Thread pool is saturated — all pool slots are occupied. New work items cannot start until a slot frees. |
| **`IdleWorkers == 0` AND `ActiveWorkers > 0`** | No spare capacity. Any spike in work will queue immediately. Combine with `async-stacks` to see what is occupying the threads. |
| **Large `WaitingToRun` count** | Work items are queued but can't start — classic thread pool starvation. Root cause: pool threads blocked synchronously (`Task.Result`, `.Wait()`, `WaitHandle.WaitOne`) holding slots without yielding. |
| **Large `WaitingForActivation` count** | Async continuations pending — awaited operations haven't completed. Very high counts point to slow I/O or external dependencies. Cross-reference with `async-stacks`. |
| **Large `Faulted` count** | Many tasks ended in exception. Cross-reference with `exception-analysis` for the exception types. |
| **Many `QueueUserWorkItemCallback` in `WorkItems`** | Direct `ThreadPool.QueueUserWorkItem` usage (non-async) — harder to track than Tasks, can saturate pool similarly. |
| **`InfoAvailable == false`** | Dump was taken without CLR runtime info or on a native-only thread pool. Thread pool counters are unavailable. |

---

## Typical output shape

```
Thread Pool Health

Worker Threads: Min=8  Max=32767  Active=312  Idle=0   ← SATURATED

Task State Distribution (2,441,882 tasks)
  WaitingForActivation    1,892,441   77%
  RanToCompletion           441,881   18%
  Running                     4,112  < 1%
  WaitingToRun                3,112  < 1%   ← queued backlog
  Faulted                       312  < 1%
  Canceled                       24  < 1%

Work Item Types
  System.Threading.Tasks.Task                      2,441,882
  System.Threading.QueueUserWorkItemCallback             312
```
