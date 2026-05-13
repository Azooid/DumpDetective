# trace-analyze

**Category:** Trace — Orchestrator  
**Included in `analyze --full`:** No (requires a trace file)

## What it does

Opens a `.nettrace` or `.etl` trace file **once** and runs all **29 trace sub-analyzers** sequentially in a single `TraceLog` parse pass, producing a single combined report with a cross-cutting **Trace Summary** dashboard at the top. More efficient than running each trace command individually because `TraceLog.OpenOrConvert` is called only once and all `Analyze(TraceLog, ...)` overloads process the already-loaded trace.

The slowest analyzers also cache per-event-name classification internally, so repeated ETW event names are classified once and reused across the rest of the trace.

Each of the 29 standalone trace commands (e.g. `GcTraceCommand`, `CpuTraceCommand`) implements `ITraceSubAnalyzer` directly — the `trace-analyze` orchestrator receives the same command instances from `TraceCommandRegistry.SubAnalyzers` and calls their `ITraceSubAnalyzer.Run()` method. No duplicate object construction occurs.

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
├─ [CPU & Allocation]       cpu-trace, alloc-trace, alloc-burst-trace
├─ [GC & Memory]            gc-trace, finalizer-trace, loh-trace
├─ [Exceptions & Locks]     exceptions-trace, contention-trace, deadlock-trace, retry-storm-trace
├─ [Threads & Concurrency]  threadpool-starvation, async-trace, context-switch-trace, task-scheduler-trace
├─ [JIT & HTTP]             jit-trace, http-trace, kestrel-trace, aspnetcore-pipeline-trace
├─ [SQL & Network]          sql-trace, json-trace, connection-pool-trace, socket-trace, dns-trace
├─ [Infrastructure]         process-lifecycle-trace, file-io-trace, handle-leak-trace
├─ [Observability]          otel-trace
├─ [Intelligence]           anomaly-trace (reads prior results), root-cause-trace (Phase 3, post-correlation)
│
├─ RenderTraceSummary(...)          ← cross-cutting dashboard
├─ RenderDiagnosticInterpretation(...)
└─ Replay all 29 sub-reports → combined IRenderSink
```

All 29 sub-analyzers operate on the same in-memory `TraceLog` — no re-parsing.

Each sub-analyzer is a standalone `ICommand` that also implements `ITraceSubAnalyzer`. `TraceCommandRegistry.SubAnalyzers` provides the single set of instances used by both the orchestrators and the registry.

---

## Sub-analyzers included

| Group | Command | Event type consumed |
|---|---|---|
| CPU & Allocation | `cpu-trace` | `SampledProfile` / `PerfInfo/Sample` |
| CPU & Allocation | `alloc-trace` | `GCAllocationTick` |
| CPU & Allocation | `alloc-burst-trace` | `GCAllocationTick` (burst pattern) |
| GC & Memory | `gc-trace` | `GC/Start`, `GC/Stop`, `GCHeapStats` |
| GC & Memory | `finalizer-trace` | `GCFinalizeObject`, `GCSuspendEE` |
| GC & Memory | `loh-trace` | `GCAllocationTick` (large objects) |
| Exceptions & Locks | `exceptions-trace` | `Exception/Start`, `ExceptionThrown` |
| Exceptions & Locks | `contention-trace` | `Contention/Start`, `Contention/Stop` |
| Exceptions & Locks | `deadlock-trace` | Lock acquire/release events |
| Exceptions & Locks | `retry-storm-trace` | Application-layer retry events |
| Threads & Concurrency | `threadpool-starvation` | `WaitHandleWaitStart`, `Adjustment` |
| Threads & Concurrency | `async-trace` | TPL `Task/Schedule`, `Task/Execute`, `Task/Completed` |
| Threads & Concurrency | `context-switch-trace` | Kernel `CSwitch` (ETL/PerfView only) |
| Threads & Concurrency | `task-scheduler-trace` | `Task/Schedule` (custom schedulers) |
| JIT & HTTP | `jit-trace` | `Method/JittingStarted`, `Method/LoadVerbose` |
| JIT & HTTP | `http-trace` | `Microsoft-AspNetCore-Hosting`, `Microsoft-Windows-ASPNET` |
| JIT & HTTP | `kestrel-trace` | `Microsoft-AspNetCore-Server-Kestrel` |
| JIT & HTTP | `aspnetcore-pipeline-trace` | `Microsoft.AspNetCore` middleware events |
| SQL & Network | `sql-trace` | `Microsoft-AdoNet-SystemData`, `Microsoft.Data.SqlClient.EventSource` |
| SQL & Network | `json-trace` | `GCAllocationTick` (JSON types) + `SampledProfile` (JSON frames) |
| SQL & Network | `connection-pool-trace` | ADO.NET connection pool events |
| SQL & Network | `socket-trace` | `System.Net.Sockets` events |
| SQL & Network | `dns-trace` | `System.Net.NameResolution` events |
| Infrastructure | `process-lifecycle-trace` | `ProcessStart`, `ProcessStop` events |
| Infrastructure | `file-io-trace` | `FileIO/Read`, `FileIO/Write` (ETL/kernel) |
| Infrastructure | `handle-leak-trace` | Handle create/close events |
| Observability | `otel-trace` | OpenTelemetry activity events |
| Intelligence | `anomaly-trace` | Reads results from prior sub-analyzers (no trace events directly) |
| Intelligence | `root-cause-trace` | Phase 3: reads correlation findings + all prior results |

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

