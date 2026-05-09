# trace-analyze

**Category:** Trace — Orchestrator  
**Included in `analyze --full`:** No (requires a trace file)

## What it does

Opens a `.nettrace` or `.etl` trace file **once** and runs all **12 trace sub-analyzers** sequentially in a single `TraceLog` parse pass, producing a single combined report with a cross-cutting **Trace Summary** dashboard at the top. More efficient than running each trace command individually because `TraceLog.OpenOrConvert` is called only once and all 12 `Analyze(TraceLog, ...)` overloads process the already-loaded trace.

The report structure:

1. **Trace Summary** — cross-cutting key metrics, signal table, pattern detector findings, top recommendations
2. **Diagnostic Interpretation** — semantic patterns detected in the CPU call tree (ORM overheads, EF materialisation, DataTable hotspots, dynamic dispatch)
3. Per-analyzer chapters grouped by area

---

## Architecture

```
TraceAnalyzeCommand.Run(args)
│
├─ TraceLog.OpenOrConvert(tracePath)          ← single open
│
├─ [CPU & Allocation]
│   ├─ CpuTraceAnalyzer            .Analyze(trace, fileName, top, processFilter, filterSystem)
│   └─ AllocTraceAnalyzer          .Analyze(trace, fileName, top, processFilter)
│
├─ [GC, Exceptions & Locks]
│   ├─ GcTraceAnalyzer             .Analyze(trace, fileName, top, processFilter)
│   ├─ ExceptionsTraceAnalyzer     .Analyze(trace, fileName, top, processFilter)
│   └─ ContentionTraceAnalyzer     .Analyze(trace, fileName, top, processFilter)
│
├─ [Threads & Concurrency]
│   ├─ ThreadPoolStarvationAnalyzer.Analyze(trace, fileName, top)
│   ├─ AsyncTraceAnalyzer          .Analyze(trace, fileName, top, processFilter)
│   └─ ContextSwitchTraceAnalyzer  .Analyze(trace, fileName, top, processFilter)
│
├─ [JIT & HTTP]
│   ├─ JitTraceAnalyzer            .Analyze(trace, fileName, top, processFilter)
│   └─ HttpTraceAnalyzer           .Analyze(trace, fileName, top, processFilter, slowMs)
│
├─ [SQL & Serialization]
│   ├─ SqlTraceAnalyzer            .Analyze(trace, fileName, top, processFilter, slowMs)
│   └─ JsonSerializationTraceAnalyzer.Analyze(trace, fileName, top, processFilter)
│
├─ RenderTraceSummary(...)          ← cross-cutting dashboard
├─ RenderDiagnosticInterpretation(...)
└─ Replay all 12 sub-reports → combined IRenderSink
```

All 12 analyzers operate on the same in-memory `TraceLog` — no re-parsing.

---

## Sub-analyzers included

| Group | Command | Event type consumed |
|---|---|---|
| CPU & Allocation | `cpu-trace` | `SampledProfile` / `PerfInfo/Sample` |
| CPU & Allocation | `alloc-trace` | `GCAllocationTick` |
| GC, Exceptions & Locks | `gc-trace` | `GC/Start`, `GC/Stop`, `GCHeapStats` |
| GC, Exceptions & Locks | `exceptions-trace` | `Exception/Start`, `ExceptionThrown` |
| GC, Exceptions & Locks | `contention-trace` | `Contention/Start`, `Contention/Stop` |
| Threads & Concurrency | `thread-pool-starvation` | `WaitHandleWaitStart`, `Adjustment` |
| Threads & Concurrency | `async-trace` | TPL `Task/Schedule`, `Task/Execute`, `Task/Completed` |
| Threads & Concurrency | `context-switch-trace` | Kernel `CSwitch` (ETL/PerfView only) |
| JIT & HTTP | `jit-trace` | `Method/JittingStarted`, `Method/LoadVerbose` |
| JIT & HTTP | `http-trace` | `Microsoft-AspNetCore-Hosting`, `Microsoft-Windows-ASPNET` |
| SQL & Serialization | `sql-trace` | `Microsoft-AdoNet-SystemData`, `Microsoft.Data.SqlClient.EventSource` |
| SQL & Serialization | `json-trace` | `GCAllocationTick` (JSON types) + `SampledProfile` (JSON frames) |

> **Note:** `context-switch-trace` requires kernel CSwitch events — only available in `.etl` traces collected with PerfView or xperf. It produces no data when running against `.nettrace` files.

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `--top <n>` | Top N items per sub-report (default: 100 for SQL; 20 for others) |
| `--process <name>` | Filter all sub-analyzers to a specific process name |
| `--show-system` | Include system/kernel frames in CPU analysis (default: hidden) |
| `--slow-ms <ms>` | Slow-command threshold for HTTP and SQL (default: 1000 ms HTTP / 500 ms SQL) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## Collecting a compatible trace

### All 12 analyzers — PerfView (ETL)

PerfView ETL traces include kernel CSwitch events required by `context-switch-trace`:

```bash
PerfView.exe /KernelEvents:Default /ClrEvents:GC,Binder,Contention,Exception,Threading,JITSymbols,Type,GCHeapAndTypeNames,Stack,Default /JITInlining /Providers:"Microsoft-AspNetCore-Hosting,Microsoft.Data.SqlClient.EventSource,System.Data.SqlClient.EventSource,Microsoft-EntityFrameworkCore" /NoGui collect
```

### All except context-switch — dotnet-trace (.nettrace)

```bash
dotnet trace collect --profile cpu-sampling \
  --providers 'Microsoft-Windows-DotNETRuntime:0xCC54:5,\
               Microsoft-AspNetCore-Hosting:0xFFFF:5,\
               Microsoft.Data.SqlClient.EventSource:0xFF:4,\
               System.Data.SqlClient.EventSource:0xFF:4' \
  -p <pid>
```

If only specific analyzers are needed, collect only the relevant providers — see each sub-command's doc for the minimum provider string.

