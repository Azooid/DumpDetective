# alloc-burst-trace

**Category:** Trace — CPU / Allocation  
**Included in `trace-analyze`:** Yes

## What it does

Identifies 500 ms windows where the allocation rate is 3× or more the median allocation rate for the trace. Allocation bursts are a leading indicator of GC pressure spikes: a short burst of large allocations can trigger a Gen2 collection even if the long-run average allocation rate looks acceptable.

---

## Analyzer: `AllocationBurstAnalyzer`

**Namespace:** `DumpDetective.Analysis.Trace.Analyzers`

Partitions `GCAllocationTick` events into 500 ms buckets. Computes the median bytes-per-bucket across the trace. Any bucket exceeding 3× the median is flagged as a burst. The top N burst windows are reported with: start time, duration, peak bytes, burst factor, and top allocating types during the window.

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `--window <ms>` | Window size in milliseconds (default: `500`) |
| `--factor <n>` | Burst factor threshold (default: `3.0`) |
| `--top <n>` | Top burst windows to show (default: `10`) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## What to look for

| Signal | What it means |
|---|---|
| Repeated bursts at regular intervals | Timer-driven batch processing allocating large buffers |
| Single massive burst early in trace | Cold-start allocation (caching or JIT); usually benign |
| Burst factor > 10× | Pathological spike; correlate with GC pauses in `gc-trace` |
| Burst types dominated by `byte[]` | Buffer pool exhaustion or unpooled I/O buffers |
