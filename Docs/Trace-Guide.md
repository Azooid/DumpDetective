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
- JIT compilation cost and warm-up overhead
- HTTP request latency and error rates
- SQL/EF query latency and slow query detection
- Async Task scheduling and sync-over-async patterns

When you have **both a trace and a dump** from the same incident, use `trace-dump-analyze` to cross-correlate both sources and surface the highest-confidence root causes.

For point-in-time heap state, object counts, and thread inspection use memory commands (see [Memory-Guide.md](Memory-Guide.md)).

---

## Fast Start

```bash
# Combined trace analysis — recommended first pass (all 10 analyzers)
DumpDetective trace-analyze app.nettrace

# Combined trace + dump analysis — highest-confidence root cause (both sources together)
DumpDetective trace-dump-analyze app.nettrace app.dmp --output incident.html

# Focused follow-ups
DumpDetective cpu-trace app.nettrace --output cpu-report.html
DumpDetective gc-trace perf.etl --process w3wp --top 50 --output gc-report.html
DumpDetective sql-trace perf.etl --process w3wp --slow-ms 500 --output sql-report.html
DumpDetective async-trace app.nettrace --output async-report.html
DumpDetective jit-trace app.nettrace --output jit-report.html
DumpDetective http-trace perf.etl --process w3wp --slow-ms 500 --output http-report.html
DumpDetective thread-pool-starvation perf.etl --top 50 --output starvation.html
```

---

## Command Index

| Command | Category | Description | Detail |
|---|---|---|---|
| `trace-analyze` | Combined | Combined report from all ten analyzers in one pass | [→](trace/Orchestrator/trace-analyze.md) |
| `trace-dump-analyze` | Combined / Cross-source | Trace + dump combined analysis with cross-source correlation | [→](trace/Orchestrator/trace-dump-analyze.md) |
| `cpu-trace` | CPU / Allocation | CPU hot path, top methods, call tree, semantic pattern detection | [→](trace/CPU-Allocation/cpu-trace.md) |
| `alloc-trace` | CPU / Allocation | Top allocating types and call sites | [→](trace/CPU-Allocation/alloc-trace.md) |
| `gc-trace` | GC / Exceptions / Locks | GC pause stats, trigger reasons, heap sizes | [→](trace/GC-Exceptions-Locks/gc-trace.md) |
| `contention-trace` | GC / Exceptions / Locks | Lock contention hotspots by total wait time | [→](trace/GC-Exceptions-Locks/contention-trace.md) |
| `exceptions-trace` | GC / Exceptions / Locks | Exception volume, top types, flood detection | [→](trace/GC-Exceptions-Locks/exceptions-trace.md) |
| `thread-pool-starvation` | Threads / Concurrency | ThreadPool starvation signal detection | [→](trace/Threads-Concurrency/threadpool-starvation.md) |
| `async-trace` | Threads / Concurrency | Async Task scheduling, sync-over-async hotspots, continuation sites | [→](trace/Threads-Concurrency/async-trace.md) |
| `jit-trace` | JIT / HTTP / SQL | JIT compilation time, slowest methods, top modules | [→](trace/JIT-HTTP/jit-trace.md) |
| `http-trace` | JIT / HTTP / SQL | HTTP request latency, top endpoints, error rates | [→](trace/JIT-HTTP/http-trace.md) |
| `sql-trace` | JIT / HTTP / SQL | SQL/EF query latency, slow queries, database summaries | [→](trace/JIT-HTTP/sql-trace.md) |

---

## Orchestration Commands

### `trace-analyze`

Runs all ten sub-analyzers (`cpu-trace`, `alloc-trace`, `gc-trace`, `contention-trace`, `exceptions-trace`, `thread-pool-starvation`, `jit-trace`, `http-trace`, `async-trace`, `sql-trace`) in a single pass over the trace file and produces a combined report. The report opens with a **Trace Summary** dashboard giving a cross-cutting health overview and pattern detector findings before the per-analyzer chapters. Start here before running focused commands.

```bash
DumpDetective trace-analyze app.nettrace
DumpDetective trace-analyze perf.etl --process w3wp --output trace-report.html
DumpDetective trace-analyze app.nettrace --top 30 --show-system
DumpDetective trace-analyze perf.etl --slow-ms 500 --output trace-report.html
```

Options: `--top <n>`, `--process <name>`, `--show-system`, `--slow-ms <ms>`, `-o <file>` — [full details →](trace/Orchestrator/trace-analyze.md)

---

### `trace-dump-analyze`

Opens a trace file AND a memory dump captured during the same incident window, runs all ten trace sub-analyzers plus a lightweight dump heap walk, then runs `TraceDumpCorrelator` to surface **cross-source findings** — patterns that require both sources to detect. The cross-source section appears at the top of the report as the highest-confidence signal.

**How to capture both files:**

```bash
# 1. Start collecting a trace
dotnet-trace collect --profile cpu-sampling -o app.nettrace
# 2. While the trace is running, take a dump
dotnet-dump collect --process-id <pid> -o app.dmp
# 3. Stop the trace
dotnet-trace stop
# 4. Analyze both together
DumpDetective trace-dump-analyze app.nettrace app.dmp --output incident.html
```

**Cross-source rules** (fire only when both trace and dump agree):

| Rule | Trace signal | Dump signal | What it means |
|---|---|---|---|
| Allocation convergence | Top allocating type | Same type dominates heap | Confirmed accumulation / leak |
| Async starvation | Starvation adjustments | High async backlog | Sync-over-async blocking |
| Exception accumulation | Exception storm | Live exception objects | Exceptions retained on heap |
| GC pause × LOH frag | High GC pause avg | LOH > 20% fragmented | Large-object pressure |
| Contention × blocked threads | High contention events | Many blocked threads | Structural lock problem |
| Slow SQL × connection count | Slow SQL commands | Many live DB connections | Connection pool under pressure |
| Pinned handles × GC pause | GC pause elevated | Many pinned handles | Heap compaction blocked |
| Finalizer queue backlog | High alloc rate | Deep finalizer queue | Finalizer thread falling behind |
| HTTP latency × async backlog | Slow HTTP requests | Stuck async state machines | Handlers blocked on async work |
| CPU saturation × idle workers | CPU > 70% avg | < 20% idle TP workers | Near saturation |

```bash
DumpDetective trace-dump-analyze app.nettrace app.dmp
DumpDetective trace-dump-analyze perf.etl crash.dmp --process w3wp --output incident.html
DumpDetective trace-dump-analyze app.nettrace app.dmp --slow-ms 500 --top 30
# Named flag form (order-independent)
DumpDetective trace-dump-analyze --trace perf.etl --dump crash.dmp --output r.html
```

Options: `--top <n>`, `--process <name>`, `--show-system`, `--slow-ms <ms>`, `--trace <file>`, `--dump <file>`, `-o <file>` — [full details →](trace/Orchestrator/trace-dump-analyze.md)

## CPU and Allocation Commands

### `cpu-trace`

Parses CPU sampling events. Reports the hot path (deepest chain of maximum CPU consumption), top methods by exclusive CPU time, and the full inclusive/exclusive call tree. System/kernel frames are hidden by default; use `--show-system` to include them.

Also runs the **semantic analysis pipeline** — four pattern detectors that identify ORM-related overheads, EF materialisation hotspots, DataTable column lookups, and dynamic/reflection dispatch from the call tree. Findings appear in the Diagnostic Interpretation chapter.

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

Parses `ContentionStart` / `ContentionStop` event pairs. Measures exact lock wait durations (stack captured at `ContentionStart`, where the thread first blocks). Groups by call site to surface the worst lock bottlenecks by total accumulated wait time.

```bash
DumpDetective contention-trace app.nettrace
DumpDetective contention-trace perf.etl --process w3wp
DumpDetective contention-trace app.nettrace --output contention.html
```

Options: `--top <n>`, `--process <name>`, `-o <file>` — [full details →](trace/GC-Exceptions-Locks/contention-trace.md)

### `exceptions-trace`

Parses first-chance exception events. Reports total count, unique type count, top N types by frequency with representative messages and throw call sites, and the most recent N individual events. Flags exception storms at > 1,000 and > 10,000 total. Pattern alerts for known expensive exception patterns (EF6 `FieldNameLookup`, assembly probe floods).

```bash
DumpDetective exceptions-trace app.nettrace
DumpDetective exceptions-trace perf.etl --process w3wp --top 40
DumpDetective exceptions-trace app.nettrace --output exceptions.html
```

Options: `--top <n>`, `--process <name>`, `-o <file>` — [full details →](trace/GC-Exceptions-Locks/exceptions-trace.md)

---

## Threads and Concurrency Commands

### `thread-pool-starvation`

Parsing `WaitHandleWaitStart` events (threads blocking on wait handles) and ThreadPool hill-climbing `Adjustment` events. Counts starvation-reason adjustments, tracks peak and final worker thread counts, and groups wait-handle events by thread and source kind (`MonitorWait`, `MonitorEnter`, `WaitOne`, `WaitAny`, `WaitAll`). Warmup-only adjustments are filtered out to reduce noise.

```bash
DumpDetective thread-pool-starvation perf.nettrace --top 50 --output starvation.html
DumpDetective thread-pool-starvation perf.etl --top 50 --output starvation.html
```

Options: `--top <n>`, `-o <file>` — [full details →](trace/Threads-Concurrency/threadpool-starvation.md)

### `async-trace`

Parses `Task/Scheduled`, `Task/Execute`, and `WaitHandleWaitStart` events to measure async Task scheduling rates, execution times, and sync-over-async hotspots. Reports the longest-running tasks, top continuation call sites, and call sites where `.Wait()` / `.Result` are called on an async operation (sync-blocking).

```bash
DumpDetective async-trace app.nettrace
DumpDetective async-trace perf.etl --process w3wp --top 30
DumpDetective async-trace app.nettrace --output async-report.html
```

Options: `--top <n>`, `--process <name>`, `-o <file>` — [full details →](trace/Threads-Concurrency/async-trace.md)

---

## JIT and HTTP Commands

### `jit-trace`

Parses `Method/JittingStarted` and `Method/LoadVerbose` event pairs to measure per-method JIT compilation time. Reports the slowest individual compilations, the most-compiled methods (dynamic/repeated JIT), and the modules with the highest compilation load. Module names are inferred from the `ModuleILPath` payload or, when absent, from the method namespace (e.g. `Microsoft.EntityFrameworkCore`).

```bash
DumpDetective jit-trace app.nettrace
DumpDetective jit-trace perf.etl --process w3wp --top 40
DumpDetective jit-trace app.nettrace --output jit-report.html
```

**Collection** — requires verbose method events:

```bash
# dotnet-trace
dotnet trace collect --providers 'Microsoft-Windows-DotNETRuntime:0x10:5' -p <pid>

# PerfView
PerfView.exe /ClrEvents:JITSymbols,Compilation,JitTracing,Default /JITInlining /NoGui collect
```

Options: `--top <n>`, `--process <name>`, `-o <file>` — [full details →](trace/JIT-HTTP/jit-trace.md)

### `http-trace`

Parses `Microsoft-AspNetCore-Hosting` (ASP.NET Core) and `Microsoft-Windows-ASPNET` (IIS/classic ASP.NET) request events. Reports request counts, average/P99 latency, error rates, top endpoints by count and latency, and slowest individual requests. Flags endpoints with P99 > 2 s or error rate > 5%.

```bash
DumpDetective http-trace perf.etl --process w3wp
DumpDetective http-trace app.nettrace --slow-ms 500 --top 30
DumpDetective http-trace perf.etl --process w3wp --output http-report.html
```

**Collection:**

```bash
# dotnet-trace (ASP.NET Core)
dotnet trace collect --providers 'Microsoft-AspNetCore-Hosting:0xFFFF:5' -p <pid>
# or
dotnet trace collect --profile asp.net -p <pid>

# PerfView (includes HTTP network capture)
PerfView.exe /ClrEvents:Default /NetworkCapture /NoGui collect
```

Options: `--top <n>`, `--process <name>`, `--slow-ms <ms>`, `-o <file>` — [full details →](trace/JIT-HTTP/http-trace.md)

### `sql-trace`

Parses `Microsoft-AdoNet-SystemData`, `Microsoft.Data.SqlClient`, and `EntityFrameworkCore` command events. Reports total SQL command count, slow query list, top queries by total execution time, and per-database summary. When `commandText` is empty (trace not collected at Verbose level), each command is labeled with its database and object identity so rows remain individually visible.

```bash
DumpDetective sql-trace perf.etl --process w3wp
DumpDetective sql-trace app.nettrace --slow-ms 500 --top 30
DumpDetective sql-trace perf.etl --process w3wp --output sql-report.html
```

**Collection — enable SQL text capture (commandText):**

```bash
# dotnet-trace (Verbose level required for commandText field)
dotnet trace collect --providers 'Microsoft-AdoNet-SystemData:0xFF:5' -p <pid>

# PerfView
PerfView.exe /Providers:"Microsoft-AdoNet-SystemData" /TraceLevel:Verbose /NoGui collect
```

> Without Verbose level, `commandText` is empty in BeginExecute events. The report shows
> each command labeled `(no SQL text, db=..., id=...)` and includes a collection guidance alert.

Options: `--top <n>`, `--process <name>`, `--slow-ms <ms>`, `-o <file>` — [full details →](trace/JIT-HTTP/sql-trace.md)

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

# JIT (verbose method events required for timing)
dotnet trace collect --providers 'Microsoft-Windows-DotNETRuntime:0x10:5' -p <pid>

# HTTP (ASP.NET Core)
dotnet trace collect --profile asp.net -p <pid>

# SQL text capture (requires Verbose level)
dotnet trace collect --providers 'Microsoft-AdoNet-SystemData:0xFF:5' -p <pid>

# Async Task events
dotnet trace collect --providers 'Microsoft-Windows-DotNETRuntime:0x11:4' -p <pid>

# Combined — all 10 analyzers (ASP.NET Core, SQL, async)
dotnet trace collect --profile cpu-sampling \
  --providers 'Microsoft-Windows-DotNETRuntime:0xCC14:5,Microsoft-AspNetCore-Hosting:0xFFFF:5,Microsoft-AdoNet-SystemData:0xFF:5' \
  -p <pid>
```

### PerfView (recommended for IIS / ETW)

```bash
# CPU + GC + contention + exceptions + JIT
PerfView.exe /ClrEvents:GC,Contention,Exception,Threading,JITSymbols,Stack,Compilation,JitTracing,Default /JITInlining /NoGui collect

# Add HTTP network capture
PerfView.exe /ClrEvents:GC,Contention,Exception,Threading,JITSymbols,Stack,Compilation,JitTracing,Default /JITInlining /NetworkCapture /NoGui collect

# Add SQL text at Verbose level
PerfView.exe /Providers:"Microsoft-AdoNet-SystemData" /TraceLevel:Verbose /NoGui collect
```

### Capturing both trace and dump (for `trace-dump-analyze`)

```bash
# 1. Start trace collection
dotnet-trace collect --profile cpu-sampling \
  --providers 'Microsoft-Windows-DotNETRuntime:0xCC14:5,Microsoft-AspNetCore-Hosting:0xFFFF:5,Microsoft-AdoNet-SystemData:0xFF:5' \
  -o app.nettrace

# 2. While trace is running, capture a dump of the same process
dotnet-dump collect --process-id <pid> -o app.dmp

# 3. Stop the trace
# (Ctrl+C in the dotnet-trace window, or: dotnet-trace stop)

# 4. Analyze both together
DumpDetective trace-dump-analyze app.nettrace app.dmp --output incident.html
```

---

## Suggested Trace Triage Path

1. Run `trace-analyze` first — get an overview across all dimensions.
2. Check the **Trace Summary** signal table and **Top Recommendations** at the top of the report.
3. Check **Diagnostic Interpretation** for semantic pattern findings (ORM overheads, EF materialisation, etc.).
4. Identify the strongest signal area: CPU, allocation, GC, contention, exceptions, starvation, JIT, HTTP, async, or SQL.
5. Run the focused command with a tuned `--top` and `--process` filter if needed.
6. Correlate with memory analysis: if you also have a dump, run `trace-dump-analyze` for cross-source findings; otherwise open the dump with `heap-stats` and compare signals manually.
7. Save outputs for comparison and sharing.

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
