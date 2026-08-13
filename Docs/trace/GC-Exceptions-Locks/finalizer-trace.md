# finalizer-trace

**Category:** Trace — GC and Memory  
**Included in `trace-analyze`:** Yes

## What it does

Detects finalization bursts, queue growth, and top finalizer types from GC events. A growing finalizer queue is a signal that objects are being allocated faster than the finalizer thread can drain them, or that finalizers are running slow due to I/O or blocking calls.

---

## Analyzer: `FinalizerTraceAnalyzer`

**Namespace:** `DumpDetective.Analysis.Trace.Analyzers`

Parses `GCFinalizersBegin` / `GCFinalizersEnd` event pairs to measure finalizer thread CPU time per GC cycle. Correlates with `GCBulkSurvivingObjectRanges` events to estimate how many finalizable objects survived each collection. The top finalizer types (by frequency in surviving ranges) are reported. Burst detection flags cycles where finalizer time exceeds 3× the median.

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `--top <n>` | Top finalizer types to show (default: `20`) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## What to look for

| Signal | What it means |
|---|---|
| Finalizer thread time > 10 ms per GC | Slow finalizer; check for I/O or blocking inside `Finalize()` |
| Queue growth across collections | Allocation rate outpacing finalization throughput |
| Same type appearing in every cycle | Consider implementing `IDisposable` and calling `GC.SuppressFinalize` |
