# thread-analysis

**Category:** Threads / Concurrency  
**Included in `analyze --full`:** Yes

## What it does

Enumerates all managed threads in the process, classifying each by category (thread pool worker, background, finalizer, GC, etc.), wait kind (monitor blocked, independent wait, none), current exception, held lock count, and stack frames. Provides a complete picture of thread activity at the moment of the dump.

---

## Analyzer: `ThreadAnalysisAnalyzer`

**Class type:** Not `IHeapObjectConsumer` — reads runtime thread structures directly  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

### How it works

Retrieves `ThreadNameMap` from `ctx.GetAnalysis<ThreadNameMap>()` — built by `ThreadNameConsumer` during the main heap walk; no second walk is needed.

For each `ClrThread t` in `ctx.Runtime.Threads`, the analyzer performs six classifications:

**Category** is determined in priority order: `t.IsFinalizer` → `"Finalizer"`, `t.IsGC` → `"GC"`, `t.IsThreadPoolWorker` → `"ThreadPool"`, a name in `ThreadNameMap` → `"Named"`, `t.IsBackground` → `"Background"`, else `"Unknown"`.

**Wait kind** is determined by inspecting up to 10 stack frames from `t.EnumerateStackTrace()`, checking the type and method name. `System.Threading.Monitor` methods `Enter`, `ReliableEnter`, `Wait`, or `TryEnter` produce `WaitKind.Monitor`. `WaitHandle.WaitOne/WaitAll/WaitAny`, `SemaphoreSlim.Wait`, `ManualResetEventSlim.Wait`, `CountdownEvent.Wait`, or `Barrier.SignalAndWait` produce `WaitKind.Independent`.

**GC mode** is `t.GCMode == GCMode.Preemptive ? "Preemptive" : "Cooperative"`.

**Exception info** is formatted as `"{TypeName}: {Message}"` when `t.CurrentException` is non-null.

**Lock info** is `"{LockCount} lock(s)"` when `t.LockCount > 0`.

**Stack frames** are only captured when `--stacks` is passed. In `analyze --full` mode stack capture is skipped to avoid I/O overhead across all threads.

---

## Pre-warm path

`ctx.GetAnalysis<ThreadAnalysisData>()` — if already run in the session (e.g. by `analyze --full`), returns immediately. `ThreadNameConsumer` pre-populates the name map from a heap walk — thread names are stored in `System.Threading.Thread` objects on the managed heap. The analyzer then only needs to read runtime thread structures (not the heap).

---

## Consumer: `ThreadNameConsumer`

**Class**: `DumpDetective.Analysis.Memory.Consumers.ThreadNameConsumer`  
**Implements**: `IHeapObjectConsumer`  
**Walk**: `DumpCollector.CollectHeapObjectsCombined`  
**Output**: `ThreadNameMap` — a `Dictionary<int managedThreadId, string name>`, cached via `ctx.PreloadAnalysis<ThreadNameMap>()`

Both `ThreadAnalysisAnalyzer` and `DeadlockAnalyzer` retrieve the pre-built `ThreadNameMap` via `ctx.GetOrCreateAnalysis<ThreadNameMap>()` with no second heap walk.

### How it works

`HeapTypeMeta.IsThread` is set for objects whose type name equals `"System.Threading.Thread"`. For each such object, the consumer reads `_managedThreadId` as an `int` field. If the ID is 0 or negative (unstarted or pooled thread), the object is skipped. The `_name` field is then read as a string; if non-empty, the mapping is recorded.

Clone/merge: each parallel clone gets its own dictionary. `MergeFrom` copies all entries from the source clone (no collision is possible since each `Thread` object has a unique managed ID).

---

## Options

| Option | Description |
|---|---|
| `--stacks` | Capture up to 10 stack frames per thread (slower, more detail) |
| `--filter <text>` | Substring match on name, category, or exception type |
| `--category <cat>` | Filter: `ThreadPool`, `Background`, `Finalizer`, `GC`, `Named` |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **Many `Monitor` waiters** | Lock contention — threads blocked inside `lock {}` blocks. Cross-reference with `deadlock-detection` to check for cycles. |
| **All thread pool workers in `Independent` wait** | Sync-over-async (`Task.Result`, `.Wait()`, `WaitHandle.WaitOne`) blocking pool threads. Thread pool starves and can't start new work. |
| **Thread with current exception** | A thread has an unhandled exception propagating at dump time. The exception may be causing the hang or crash. |
| **Very high thread count (thousands)** | Thread-per-request pattern or `Thread.Start()` in a loop — not using the pool. Thread creation is expensive and unthrottled. |
| **Finalizer thread in `Monitor` or `Independent` wait** | Finalization is blocked — unmanaged resources cannot be released. See `finalizer-queue`. |
| **Named threads** | Application-defined threads (e.g. `"BackgroundSync"`, `"Heartbeat"`) — use these to identify which background services are active. |
| **GC mode = Cooperative on many threads** | Threads executing in GC-unfriendly code (native interop) or suspended in cooperative mode — may delay GC. |

---

## Typical output shape

```
Thread Analysis — 412 threads  (318 alive, 94 dead)

Managed ID   OS TID    Category         Wait Kind    Name / Exception
         1     4812    Finalizer         None         —
         2     8220    GC                None         —
         4     9114    ThreadPool        Monitor      —
         6    10440    ThreadPool        Monitor      —
        82    44112    Named/Background  None         "BackgroundSync"
       124    48820    ThreadPool        Independent  System.TimeoutException

Summary
  Monitor-blocked:    212 threads
  Independent-waiting: 88 threads
  With exception:      24 threads
  Named threads:        8 threads
```
