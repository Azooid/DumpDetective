# threadpool-starvation

**Category:** Trace — Threads / Concurrency  
**Included in `analyze --full`:** No (requires a trace file)

## What it does

Parses `WaitHandleWaitStart` events and ThreadPool hill-climbing `Adjustment` events from a `.nettrace` or `.etl` file to detect ThreadPool starvation. Identifies threads blocking on wait handles (the symptom) and hill-climbing adjustments where the reason is `Starvation` (the ThreadPool's own diagnosis). Produces a count of starvation events, a timeline of thread-count adjustments, and a breakdown of wait-handle sources by thread.

---

## Analyzer: `ThreadPoolStarvationAnalyzer`

**Namespace**: `DumpDetective.Analysis.Trace.Analyzers`

### How it works

Opens the trace via `TraceLog.OpenOrConvert(tracePath)` and iterates all events. Two event patterns are processed:

**`WaitHandleWaitStart`** events record which thread is blocking and on what kind of wait. The wait source is an integer payload field `WaitSource` mapped to a canonical name: `MonitorWait` (1), `MonitorEnter` (2), `WaitOne` (3), `WaitAny` (4), `WaitAll` (5), `Unknown` (0 or unrecognized). These are grouped by `(ThreadId, WaitSourceName)` and the top N groups by event count are reported.

**`Adjustment`** events record the ThreadPool hill-climbing algorithm's decisions. For each event, the `Reason` payload field is mapped to a canonical string: `Warmup` (0), `Initializing` (1), `RandomMove` (2), `ClimbingMove` (3), `ChangePoint` (4), `Stabilizing` (5), `Starvation` (6), `ThreadTimedOut` (7), `CooperativeBlocking` (8). The `NewWorkerThreadCount` payload tracks the current thread count ceiling, and `AverageThroughput` records the measured throughput at the time of adjustment. Adjustments with reason `Starvation` are counted separately as the primary starvation signal. The most recent 50 adjustment records are retained for the timeline display.

`StarvationEventCount` is the total number of `Adjustment` events with reason `Starvation`. `PeakWorkerThreadCount` is the highest `NewWorkerThreadCount` seen. `FinalWorkerThreadCount` is the last value observed.

---

## Collecting the trace

```bash
# dotnet-trace — threadpool events
dotnet trace collect \
  --providers 'Microsoft-Windows-DotNETRuntime:0x10000:4' -p <pid>

# Full CPU + threadpool (recommended)
dotnet trace collect --profile cpu-sampling \
  --providers 'Microsoft-Windows-DotNETRuntime:0x10040:5' -p <pid>

# PerfView
PerfView /ThreadTime collect
```

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `--top <n>` | Top N wait-handle event groups to show (default: 10) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## What to look for

| Signal | What it means |
|---|---|
| **`StarvationEventCount > 0`** | The ThreadPool hill-climber detected starvation — worker threads were exhausted and throughput dropped. |
| **Many `MonitorWait` or `MonitorEnter` per thread** | Threads blocking on `lock` statements or `Monitor.Wait` inside ThreadPool workers — classic sync-over-async or lock-inside-worker pattern. |
| **`PeakWorkerThreadCount` much higher than CPU core count** | The ThreadPool was forced to inject many threads to compensate for blocking — indicates synchronous blocking on pool threads. |
| **Repeated `ClimbingMove` → `Starvation` → `ClimbingMove` cycle** | The pool can't stabilize — blocking throughput is high enough that new threads keep being injected and then starving. |
| **`WaitOne` events on many threads** | Manual `WaitHandle.WaitOne` calls on pool threads — these block the thread completely, preventing any work dispatch. Replace with `await`-based alternatives. |
| **`CooperativeBlocking` adjustment reason** | .NET 6+ cooperative blocking detected — the pool is adapting, but the root cause (sync blocking) should still be addressed. |
