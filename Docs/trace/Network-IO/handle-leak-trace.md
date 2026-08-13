# handle-leak-trace

**Category:** Trace — Network and I/O  
**Included in `trace-analyze`:** Yes

## What it does

Detects GCHandle leaks by comparing the count of `GCCreateHandle` events against `GCDestroyHandle` events over the trace window. A growing delta indicates that `GCHandle.Alloc` calls are not being matched by corresponding `GCHandle.Free` / `Dispose` calls.

---

## Analyzer: `HandleLeakTraceAnalyzer`

**Namespace:** `DumpDetective.Analysis.Trace.Analyzers`

Parses `GCCreateHandle` (with handle kind and object type) and `GCDestroyHandle` events. Tracks a running counter per handle kind (`Pinned`, `Strong`, `WeakShort`, `WeakLong`, `AsyncPinned`). A positive net count at the end of the trace indicates unbalanced handles. The top leaking handle kinds and their associated object types are reported.

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## What to look for

| Signal | What it means |
|---|---|
| Net positive `Pinned` count | Buffers pinned for I/O and not released; memory not reclaimable by GC |
| Net positive `Strong` count | `GCHandle.Alloc` used without corresponding `Free`; classic interop leak |
| Leak rate proportional to request rate | Per-request handle allocation without disposal |
