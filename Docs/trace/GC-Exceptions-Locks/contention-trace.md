# contention-trace

**Category:** Trace — GC / Exceptions / Locks  
**Included in `analyze --full`:** No (requires a trace file)

## What it does

Parses `ContentionStart`/`ContentionStop` event pairs from a `.nettrace` or `.etl` trace to report lock contention hotspots. For each contention event, measures the exact wait duration and identifies the call site (first non-runtime user-code frame). Aggregates by call site to surface the worst lock bottlenecks.

---

## Analyzer: `ContentionTraceAnalyzer`

**Namespace**: `DumpDetective.Analysis.Trace.Analyzers`

### How it works

Opens the trace via `TraceLog.OpenOrConvert(tracePath)` and iterates all events. `ContentionStart` / `Contention/Start` events are recorded in a `pending` dictionary keyed by `ev.ThreadID`, storing the `TimeStampRelativeMSec`. When the matching `ContentionStop` / `Contention/Stop` arrives, the wait duration is computed as the difference between the stop and start timestamps.

The call site (the first non-runtime user-code frame) is resolved from the `ContentionStop` stack by walking from leaf to root and skipping frames whose `FullMethodName` contains `Monitor`, `JIT_MonEnter`, `clr!`, `ntdll`, or `KERNELBASE`. If no user frame is found, the module name is used as a fallback.

Results are accumulated into two structures: a `hotspots` dictionary keyed by call site frame (accumulating event count, total wait ms, and worst single wait), and an `events` list for displaying worst individual waits. `TopHotspots` is sorted by total wait time descending. `ThreadsHit` is the distinct thread ID count across all contention events.

---

## Collecting the trace

```bash
# dotnet-trace — contention provider
dotnet trace collect \
  --providers 'Microsoft-Windows-DotNETRuntime:0x4000:4' -p <pid>

# PerfView
PerfView /ContentionStacks collect

# dotnet-trace with full CPU + contention
dotnet trace collect --profile cpu-sampling \
  --providers 'Microsoft-Windows-DotNETRuntime:0xCC14:5' -p <pid>
```

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `--top <n>` | Top N hotspots and worst-wait events to show (default: 20) |
| `--process <name>` | Substring match on process name |
| `-o, --output <file>` | Output path |

---

## What to look for

| Signal | What it means |
|---|---|
| **Single call site with very high total wait time** | A heavily contested lock — this is the lock bottleneck. Consider lock-free data structures (`ConcurrentDictionary`), reader/writer locks, or partitioned locks. |
| **Many threads hit the same contention site** | The lock is a throughput bottleneck — all concurrent requests serialize through it. |
| **Contention in `Dictionary` or `List` methods** | Application collections not protected correctly. Check if using `lock` around mutable shared state. |
| **`Monitor.Enter` inside `async` methods** | Holding a monitor lock across `await` — the thread pool thread may return while holding the lock, causing other waiters to block indefinitely. Use `SemaphoreSlim` instead. |
| **High total contention time vs. trace duration** | The process is spending significant time waiting for locks rather than executing. May correlate with high CPU time in `Monitor.Enter` in `cpu-trace`. |
| **MaxWait >> AvgWait** | Occasional very long lock waits — check for lock convoys or priority inversion. |
