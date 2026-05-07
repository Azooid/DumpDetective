# DumpDetective Trace Guide

Complete standalone reference for trace workflows and commands. For the full documentation index, see [documentation.md](documentation.md).

---

## Supported Inputs

- `.nettrace`
- `.etl`

`.etl.zip` is not supported.

---

## When to Use Trace Commands

Use trace analysis for time-based behavior — things that happen during execution, not what is frozen in memory at a point in time:

- CPU hotspots and call trees
- Allocation churn and allocation site concentration
- GC pause outliers and trigger reasons
- Lock contention hotspots
- Exception flood patterns
- ThreadPool starvation patterns

For point-in-time heap state, object counts, and thread inspection use memory commands (see [Memory-Guide.md](Memory-Guide.md)).

---

## Fast Start

```bash
# Combined trace analysis — recommended first pass
DumpDetective trace-analyze app.nettrace

# Focused follow-ups
DumpDetective cpu-trace app.nettrace --output cpu-report.html
DumpDetective gc-trace perf.etl --process w3wp --top 50 --output gc-report.html
DumpDetective threadpool-starvation perf.etl --top 50 --output starvation.html
```

---

## Command Index

| Command | Category | Description | Detail |
|---|---|---|---|
| `trace-analyze` | Orchestration | Combined report from all six analyzers | [→](trace/Orchestrator/trace-analyze.md) |
| `cpu-trace` | CPU / Allocation | CPU hot path, top methods, call tree | [→](trace/CPU-Allocation/cpu-trace.md) |
| `alloc-trace` | CPU / Allocation | Top allocating types and call sites | [→](trace/CPU-Allocation/alloc-trace.md) |
| `gc-trace` | GC / Exceptions / Locks | GC pause stats, trigger reasons, heap sizes | [→](trace/GC-Exceptions-Locks/gc-trace.md) |
| `contention-trace` | GC / Exceptions / Locks | Lock contention hotspots by total wait time | [→](trace/GC-Exceptions-Locks/contention-trace.md) |
| `exceptions-trace` | GC / Exceptions / Locks | Exception volume, top types, flood detection | [→](trace/GC-Exceptions-Locks/exceptions-trace.md) |
| `threadpool-starvation` | Threads / Concurrency | ThreadPool starvation signal detection | [→](trace/Threads-Concurrency/threadpool-starvation.md) |

---

## Orchestration Commands

### `trace-analyze`

Runs all six sub-analyzers (`cpu-trace`, `alloc-trace`, `gc-trace`, `contention-trace`, `exceptions-trace`, `threadpool-starvation`) in a single pass over the trace file and produces a combined report. Start here before running focused commands.

```bash
DumpDetective trace-analyze app.nettrace
DumpDetective trace-analyze perf.etl --process w3wp --output trace-report.html
DumpDetective trace-analyze app.nettrace --top 30 --show-system
```

Options: `--top <n>`, `--process <name>`, `--show-system`, `-o <file>` — [full details →](trace/Orchestrator/trace-analyze.md)

---

## CPU and Allocation Commands

### `cpu-trace`

Parses CPU sampling events. Reports the hot path (deepest chain of maximum CPU consumption), top methods by exclusive CPU time, and the full inclusive/exclusive call tree. System/kernel frames are hidden by default; use `--show-system` to include them.

```bash
DumpDetective cpu-trace app.nettrace
DumpDetective cpu-trace perf.etl --top 40 --process w3wp
DumpDetective cpu-trace app.nettrace --output cpu-report.html
```

Options: `--top <n>`, `--process <name|pid>`, `--show-system`, `-o <file>` — [full details →](trace/CPU-Allocation/cpu-trace.md)

### `alloc-trace`

Parses sampled `GCAllocationTick` events (fires ~every 100 KB allocated per thread). Reports the top allocating types by estimated byte volume and the top allocating call sites (first non-system user-code frame on the allocation stack). Totals are estimates proportional to actual allocation volume.

```bash
DumpDetective alloc-trace app.nettrace
DumpDetective alloc-trace perf.etl --process w3wp --top 30
DumpDetective alloc-trace app.nettrace --output alloc.html
```

Options: `--top <n>`, `--process <name>`, `-o <file>` — [full details →](trace/CPU-Allocation/alloc-trace.md)

---

## GC, Exceptions, and Lock Commands

### `gc-trace`

Parses `GC/Start` / `GC/Stop` event pairs and `GCHeapStats` events. Reports per-generation pause counts and statistics, the longest N pauses with trigger reasons and heap size before/after, and flags blocking Gen2 pauses over 100 ms and explicit `GC.Collect()` calls (`Induced` reason).

```bash
DumpDetective gc-trace app.nettrace
DumpDetective gc-trace perf.etl --process w3wp --top 50
DumpDetective gc-trace app.nettrace --output gc-report.html
```

Options: `--top <n>`, `--process <name>`, `-o <file>` — [full details →](trace/GC-Exceptions-Locks/gc-trace.md)

### `contention-trace`

Parses `ContentionStart` / `ContentionStop` event pairs. Measures exact lock wait durations. Groups by call site (first non-runtime user-code frame) to surface the worst lock bottlenecks by total accumulated wait time.

```bash
DumpDetective contention-trace app.nettrace
DumpDetective contention-trace perf.etl --process w3wp
DumpDetective contention-trace app.nettrace --output contention.html
```

Options: `--top <n>`, `--process <name>`, `-o <file>` — [full details →](trace/GC-Exceptions-Locks/contention-trace.md)

### `exceptions-trace`

Parses first-chance exception events. Reports total count, unique type count, top N types by frequency with representative messages and throw call sites, and the most recent N individual events. Flags exception storms at > 1,000 and > 10,000 total.

```bash
DumpDetective exceptions-trace app.nettrace
DumpDetective exceptions-trace perf.etl --process w3wp --top 40
DumpDetective exceptions-trace app.nettrace --output exceptions.html
```

Options: `--top <n>`, `--process <name>`, `-o <file>` — [full details →](trace/GC-Exceptions-Locks/exceptions-trace.md)

---

## Threads and Concurrency Commands

### `threadpool-starvation`

Parses `WaitHandleWaitStart` events (threads blocking on wait handles) and ThreadPool hill-climbing `Adjustment` events. Counts starvation-reason adjustments, tracks peak and final worker thread counts, and groups wait-handle events by thread and source kind (`MonitorWait`, `MonitorEnter`, `WaitOne`, `WaitAny`, `WaitAll`).

```bash
DumpDetective threadpool-starvation perf.nettrace --top 50 --output starvation.html
DumpDetective threadpool-starvation perf.etl --top 50 --output starvation.html
```

Options: `--top <n>`, `-o <file>` — [full details →](trace/Threads-Concurrency/threadpool-starvation.md)

---

## Collecting Traces

### dotnet-trace (recommended for .NET 6+)

```bash
# CPU sampling
dotnet trace collect --profile cpu-sampling -p <pid>

# GC events (includes GCAllocationTick)
dotnet trace collect --profile gc-verbose -p <pid>

# Exceptions
dotnet trace collect --providers 'Microsoft-Windows-DotNETRuntime:0x8014:5' -p <pid>

# Contention
dotnet trace collect --providers 'Microsoft-Windows-DotNETRuntime:0x4000:4' -p <pid>

# ThreadPool
dotnet trace collect --providers 'Microsoft-Windows-DotNETRuntime:0x10000:4' -p <pid>

# Combined CPU + GC + contention + ThreadPool
dotnet trace collect --profile cpu-sampling \
  --providers 'Microsoft-Windows-DotNETRuntime:0xCC14:5' -p <pid>
```

### PerfView (recommended for IIS / ETW)

```bash
PerfView /KernelEvents=default /ClrEvents=default collect
PerfView /GCOnly collect
PerfView /ContentionStacks collect
PerfView /ThreadTime collect
```

---

## Suggested Trace Triage Path

1. Run `trace-analyze` first — get an overview across all dimensions.
2. Identify the strongest signal area: CPU, allocation, GC, contention, exceptions, or starvation.
3. Run the focused command with a tuned `--top` and `--process` filter if needed.
4. Correlate with memory analysis: if GC pressure is high, open the corresponding dump with `heap-stats` and `alloc-trace` together.
5. Save outputs for comparison and sharing.

### Common Correlations

| Trace signal | Follow-up memory command |
|---|---|
| High allocation rate (`alloc-trace`) | `heap-stats`, `gen-summary`, `string-duplicates` |
| GC heap not shrinking after Gen2 | `memory-leak`, `static-refs` |
| Exception storm (`exceptions-trace`) | `exception-analysis` in dump |
| ThreadPool starvation | `thread-pool`, `async-stacks`, `deadlock-detection` |
| Lock contention on `Dictionary`/`List` | `high-refs`, `thread-analysis --blocked-only` |
