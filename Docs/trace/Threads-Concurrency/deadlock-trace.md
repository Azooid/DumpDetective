# deadlock-trace

**Category:** Trace — Exceptions and Locks  
**Included in `trace-analyze`:** Yes

## What it does

Heuristic detection of mutually-blocked thread pairs from `ContentionStart` / `ContentionStop` events. A deadlock signature is two or more threads each waiting on a lock held by another thread in the set, with no `ContentionStop` event arriving within a configurable timeout.

Unlike `deadlock-detection` (dump-based), this command operates on trace events and can identify deadlocks that were transient — they occurred during the trace window but may have been resolved by the time a dump was captured.

---

## Analyzer: `DeadlockPatternAnalyzer`

**Namespace:** `DumpDetective.Analysis.Trace.Analyzers`

Builds a thread-wait graph from `ContentionStart` events (thread ID + lock address). When a thread still has an unmatched `ContentionStart` after the timeout, it is considered a potential deadlock participant. The analyzer then looks for cycles in the thread-wait graph. Suspected deadlock groups are reported with thread IDs, lock addresses, and the elapsed wait time.

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `--timeout <ms>` | Minimum wait time to flag as suspect (default: `5000`) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## What to look for

| Signal | What it means |
|---|---|
| 2-thread cycle with long wait | Classic `lock(A) { lock(B) }` vs. `lock(B) { lock(A) }` deadlock |
| Many threads waiting on same lock address | Lock convoy — not a deadlock but severe contention |
| Cycle resolved mid-trace | Deadlock was broken by timeout or watchdog; check for retry loops |
