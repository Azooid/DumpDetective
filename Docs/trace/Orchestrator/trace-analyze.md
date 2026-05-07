# trace-analyze

**Category:** Trace — Orchestrator  
**Included in `analyze --full`:** No (requires a trace file)

## What it does

Opens a `.nettrace` or `.etl` trace file **once** and runs all six trace sub-analyzers sequentially in a single `TraceLog` parse pass, producing a single combined report. More efficient than running each trace command individually because `TraceLog.OpenOrConvert` is called only once and all six `Analyze(TraceLog, ...)` overloads process the already-loaded trace.

---

## Architecture

```
TraceAnalyzeCommand.Run(args)
│
├─ TraceLog.OpenOrConvert(tracePath)  ← single open
│
├─ CpuTraceAnalyzer       .Analyze(trace, fileName, top, processFilter, filterSystem)
├─ AllocTraceAnalyzer     .Analyze(trace, fileName, top, processFilter)
├─ GcTraceAnalyzer        .Analyze(trace, fileName, top, processFilter)
├─ ContentionTraceAnalyzer.Analyze(trace, fileName, top, processFilter)
├─ ExceptionsTraceAnalyzer.Analyze(trace, fileName, top, processFilter)
├─ ThreadPoolStarvationAnalyzer.Analyze(trace, fileName, top)
│
└─ Replay all 6 sub-reports → combined IRenderSink
```

All six analyzers operate on the same in-memory `TraceLog` — no re-parsing.

---

## Sub-analyzers included

| Group | Command | Event type consumed |
|---|---|---|
| CPU / Allocation | `cpu-trace` | `SampledProfile` / `PerfInfo/Sample` |
| CPU / Allocation | `alloc-trace` | `GCAllocationTick` |
| GC / Exceptions / Locks | `gc-trace` | `GC/Start`, `GC/Stop`, `GCHeapStats` |
| GC / Exceptions / Locks | `exceptions-trace` | `Exception/Start`, `ExceptionThrown` |
| GC / Exceptions / Locks | `contention-trace` | `Contention/Start`, `Contention/Stop` |
| Threads / Concurrency | `threadpool-starvation` | `WaitHandleWaitStart`, `Adjustment` |

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `-n, --top <n>` | Top N items per sub-report (default: 20) |
| `--process <name>` | Filter all sub-analyzers to a specific process name |
| `--show-system` | Include system/kernel frames in CPU analysis (default: hidden) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## Collecting a compatible trace

To get data for all 6 sub-analyzers in a single collection:

```bash
# dotnet-trace — CPU + alloc + GC + exceptions + contention + threadpool
dotnet trace collect --profile cpu-sampling \
  --providers 'Microsoft-Windows-DotNETRuntime:0xCC14:5' -p <pid>

# PerfView — all events
PerfView /KernelEvents=default /ClrEvents=default collect

# dotnet-trace minimum for all 6 analyzers
dotnet trace collect \
  --providers 'Microsoft-Windows-DotNETRuntime:0xCC14:5,Microsoft-DotNETRuntime-ThreadPool:0xFF:5' \
  -p <pid>
```

If only specific analyzers are needed, collect only the relevant providers — see each sub-command's doc for the minimum provider string.
