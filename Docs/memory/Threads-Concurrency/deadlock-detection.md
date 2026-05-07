# deadlock-detection

**Category:** Threads / Concurrency  
**Included in `analyze --full`:** Yes

## What it does

Detects potential deadlock cycles in the process by building a wait-for graph from `Monitor` locks and running DFS cycle detection. Reports which threads are involved in deadlocks, which locks they are waiting on, and which they hold. Also reports non-cycling monitor waiters and independent-wait threads waiting on `WaitHandle`, `SemaphoreSlim`, and similar primitives.

---

## Analyzer: `DeadlockAnalyzer`

**Class type:** Not `IHeapObjectConsumer` — reads runtime thread and heap sync-block structures  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

### How it works

The analyzer runs five sequential steps.

**Step 1 — Thread lookup tables.** Builds two dictionaries indexed by `t.Address` and `t.ManagedThreadId` respectively, for O(1) thread resolution during sync block and stack frame processing.

**Step 2 — Enumerate sync blocks.** Calls `ctx.Heap.EnumerateSyncBlocks()` and filters for `sb.IsMonitorHeld == true && sb.Object != 0`. For each held monitor lock, records the lock object's address and CLR type name (resolved via `ctx.Heap.GetObject`), the owning thread's managed ID, OS ID, and name from `ThreadNameMap`, and the recursion count from `sb.MonitorRecursionCount`.

**Step 3 — Identify monitor waiters (heuristic).** ClrMD 3.x does not expose per-sync-block waiter lists. Waiters are approximated by scanning each managed thread's top 20 stack frames for `System.Threading.Monitor` methods: `Enter`, `ReliableEnter`, `TryEnter`, or `Wait`. Threads that match and have `LockCount == 0` (not currently holding a lock) are treated as monitor waiter candidates. They are associated with the lock whose `LockTypeName` matches the type being acquired — a best-effort heuristic since ClrMD cannot definitively link a waiting thread to a specific lock without native stack data.

**Step 4 — Identify independent waiters.** A separate frame scan looks for `WaitHandle.WaitOne/WaitAll/WaitAny`, `SemaphoreSlim.Wait/WaitAsync`, `ManualResetEventSlim.Wait`, `CountdownEvent.Wait`, and `Barrier.SignalAndWait`. For each matching thread the block reason, top user frame (first non-System frame), and up to 20 stack frames are recorded.

**Step 5 — Build wait-for graph and detect cycles.** Constructs a directed `waiter → owner` graph from the monitor lock entries: for each waiter `w` of a lock held by owner `o`, adds edge `w → o`. Standard DFS cycle detection runs over this graph; any cycle found is a confirmed deadlock. `ThreadNameMap` (populated by `ThreadNameConsumer` during the main heap walk and retrieved via `ctx.GetAnalysis<ThreadNameMap>()`) enriches cycle output with human-readable thread names.

---

## Pre-warm path

`ctx.GetAnalysis<DeadlockData>()` — if already cached in session, returns immediately. `ThreadNameMap` is read from `ctx.GetAnalysis<ThreadNameMap>()` (populated by `ThreadNameConsumer` during the main heap walk).

---

## Consumer: `ThreadNameConsumer` (shared)

**Class**: `DumpDetective.Analysis.Memory.Consumers.ThreadNameConsumer`  
**Walk**: `DumpCollector.CollectHeapObjectsCombined` (same instance as used by `thread-analysis`)  
**Purpose**: Maps managed thread IDs to thread name strings. `DeadlockAnalyzer` calls `ctx.GetAnalysis<ThreadNameMap>()` to retrieve the pre-populated map for enriching deadlock cycle output with human-readable names.

See [thread-analysis.md](thread-analysis.md#consumer-threadnameconsumer) for the full `Consume()` / `MergeFrom()` implementation. No second heap walk is needed here — the same `ThreadNameMap` instance is shared across all consumers that need it.

---

## Disk cache

None.

---

## Options

No options — always runs the full detection pass.

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **`ConfirmedCycles` non-empty** | Hard deadlock — the listed threads will never make progress. Process restart required. Identify the lock types and fix acquisition ordering. |
| **Two-thread cycle** | Classic AB-BA deadlock. Thread A holds Lock X and waits for Lock Y; Thread B holds Lock Y and waits for Lock X. Fix: always acquire locks in the same canonical order. |
| **Three+ thread cycle** | Convoy deadlock — rarer, harder to reproduce. Fix: reduce lock granularity or use a higher-level concurrency primitive. |
| **Many `IndependentWaiters` with `WaitHandle.WaitOne`** | Sync-over-async: threads block on wait handles while holding pool slots. May starve the pool even without a deadlock cycle. |
| **`RecursionCount` high** | Same thread re-entered the monitor many times — legal but suspicious if unexpected (may indicate a bug in recursive call chain). |
| **Finalizer thread in a cycle** | Critical — finalization is stopped, unmanaged resources cannot be freed, memory leak accelerates. |
| **Waiter count > 50 on a single lock** | Lock hotspot — one lock is the serialization bottleneck for dozens of threads. Consider `ConcurrentDictionary`, `ReaderWriterLockSlim`, or partitioned locks. |

---

## Typical output shape

```
Deadlock Detection

⚠ DEADLOCK CYCLE DETECTED — 2 threads

Thread 12 (OS 4812)  "BackgroundWorker"
  → Waiting on: System.Object @ 0xFFFF1234  (held by Thread 8)
  → Holds: System.Collections.Generic.Dictionary`2 @ 0xFFFE4421

Thread 8 (OS 8220)  "RequestProcessor"
  → Waiting on: System.Collections.Generic.Dictionary`2 @ 0xFFFE4421  (held by Thread 12)
  → Holds: System.Object @ 0xFFFF1234

Monitor Locks — 14 held locks total, 1 cycle
  System.String @ 0xFFF0001  held by Thread 24  •  3 waiters  •  recursion: 1
  System.Object @ 0xFFF0002  held by Thread 36  •  1 waiter   •  recursion: 1

Independent Waiters — 88 threads
  Thread 44 (OS 9114)  WaitHandle.WaitOne  →  MyApp.DataService.GetFromCache
  Thread 46 (OS 9200)  SemaphoreSlim.Wait  →  MyApp.RateLimiter.Acquire
```
