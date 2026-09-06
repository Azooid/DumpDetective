# DumpDetective Documentation

DumpDetective is a .NET AOT CLI tool for analyzing Windows memory dumps (`.dmp` / `.mdmp`), .NET performance traces (`.nettrace` / `.etl`), and Chrome DevTools performance traces (`.json` / `.json.gz`). It runs commands for heap statistics, memory leak detection, deadlock diagnosis, GC analysis, CPU profiling, allocation hotspots, browser JS/DOM leak and jank detection, and more — producing output as HTML, Markdown, JSON, or plain text.

---

## Quick Start

```bash
# Full incident report from a dump
DumpDetective analyze app.dmp --full --output report.html

# Combined trace + dump cross-source analysis
DumpDetective trace-dump-analyze app.nettrace app.dmp --output report.html

# Combined trace analysis only
DumpDetective trace-analyze app.nettrace --output trace.html

# Re-render a saved report in a different format
DumpDetective render report.bin --output report.md

# Chrome DevTools performance trace (browser JS/DOM/CPU/network) analysis
DumpDetective web-analyze trace.json.gz --output report.html
```

---

## Documentation Sections

| Section | Contents |
|---|---|
| [Architecture](Architecture.md) | Project layout, dependency graph, data-flow pipeline, and extension points |
| [Memory Analysis Guide](Memory-Guide.md) | Workflows, triage paths, and all memory command options |
| [Trace Analysis Guide](Trace-Guide.md) | Workflows and all trace command options |
| [Cache Inventory](cache.md) | Built-in system caches, storage locations, lifecycle, and cleanup |
| [Plugin System](Plugins.md) | How to write and install external plugin commands |
| [Memory Commands](#memory-commands) | All 38 dump-analysis commands with links to detailed docs |
| [Trace Commands](#trace-commands) | All 31 .NET trace commands with links to detailed docs |
| [Web Performance Commands](#web-performance-commands) | All 8 Chrome DevTools trace commands |

---

## Memory Commands

Commands that operate on `.dmp` / `.mdmp` dump files.

### Orchestration

| Command | Description | In `--full` |
|---|---|:---:|
| [analyze](memory/Orchestrator/analyze.md) | Scored health report for one dump; optionally runs all sub-commands | — |
| [trend-analysis](memory/Orchestrator/trend-analysis.md) | Multi-dump trend report: heap growth, type counts, GC metrics across captures | — |
| [diagnose](memory/Targeted-Interactive/diagnose.md) | Synthesize all analysis into an executive + engineering diagnostic summary | — |

### Replay and Comparison

| Command | Description | In `--full` |
|---|---|:---:|
| [render](memory/Replay-Comparison/render.md) | Convert a saved `.json`/`.bin` report to any output format without re-analyzing | — |
| [diff](memory/Replay-Comparison/diff.md) | Compare two saved reports side-by-side; highlights changes, new alerts, deltas | — |

### Cache Lifecycle

| Command | Description | In `--full` |
|---|---|:---:|
| [load](memory/Cache-Lifecycle/load.md) | Pre-build all analysis caches so subsequent runs complete in seconds | — |
| [close](memory/Cache-Lifecycle/close.md) | Delete `.ddcache` directories to reclaim disk space | — |

### Heap Overview

| Command | Description | In `--full` |
|---|---|:---:|
| [heap-stats](memory/Heap-Memory/heap-stats.md) | Top types by total size and instance count across the entire heap | ✓ |
| [gen-summary](memory/Heap-Memory/gen-summary.md) | Heap bytes and object counts broken down by GC generation (Gen0–POH) | ✓ |
| [memory-pressure](memory/Heap-Memory/memory-pressure.md) | Unified view of managed heap, GC generations, and thread stack memory usage | ✓ |

### Heap Allocation and Fragmentation

| Command | Description | In `--full` |
|---|---|:---:|
| [heap-fragmentation](memory/Heap-Memory/heap-fragmentation.md) | Free-hole analysis per heap segment; live/free byte ratio per segment | ✓ |
| [large-objects](memory/Heap-Memory/large-objects.md) | Individual objects ≥ 85 KB on the LOH; aggregated by type | ✓ |
| [pinned-objects](memory/Heap-Memory/pinned-objects.md) | Objects pinned via GC handles; grouped by type and handle kind | ✓ |

### Memory Leaks

| Command | Description | In `--full` |
|---|---|:---:|
| [memory-leak](memory/Heap-Memory/memory-leak.md) | Suspects ranked by count and size; root-chain traces to GC roots | ✓ |
| [high-refs](memory/Heap-Memory/high-refs.md) | Most-referenced objects by inbound reference count | ✓ |
| [string-duplicates](memory/Heap-Memory/string-duplicates.md) | Duplicate string groups sorted by wasted bytes | ✓ |

### Retention / Leak Signals

| Command | Description | In `--full` |
|---|---|:---:|
| [cache-patterns](memory/Retention-Leak/cache-patterns.md) | Detect unbounded Dictionary / MemoryCache / HashSet instances by entry count | ✓ |
| [closure-capture](memory/Retention-Leak/closure-capture.md) | Find compiler-generated closure display-class objects capturing large graphs | ✓ |
| [datatable-amp](memory/Retention-Leak/datatable-amp.md) | Measure DataTable / DataSet memory amplification vs. typed collections | ✓ |

### GC / Lifetime

| Command | Description | In `--full` |
|---|---|:---:|
| [finalizer-queue](memory/Heap-Memory/finalizer-queue.md) | Types queued for finalization; resurrection detection | ✓ |
| [handle-table](memory/Heap-Memory/handle-table.md) | GC handle table breakdown by kind: Strong, WeakShort, Pinned, Async, Dependent | ✓ |
| [static-refs](memory/Heap-Memory/static-refs.md) | Statically-rooted object trees; optional BFS retained-size computation | ✓ |
| [weak-refs](memory/Heap-Memory/weak-refs.md) | Weak GC handles — alive vs. collected object breakdown | ✓ |
| [event-analysis](memory/Heap-Memory/event-analysis.md) | Event fields with high subscriber counts; potential event-handler leaks | ✓ |
| [gc-root-map](memory/Heap-Memory/gc-root-map.md) | Classify all GC roots by kind and show top types held per root category | ✓ |

### Exceptions and Diagnostics

| Command | Description | In `--full` |
|---|---|:---:|
| [exception-analysis](memory/Exceptions-Diagnostics/exception-analysis.md) | Live exception instances by type; message samples and stack frames | ✓ |

### Threads and Concurrency

| Command | Description | In `--full` |
|---|---|:---:|
| [thread-analysis](memory/Threads-Concurrency/thread-analysis.md) | All threads classified by state, wait kind, GC mode, and top stack frames | ✓ |
| [thread-pool](memory/Threads-Concurrency/thread-pool.md) | ThreadPool counters; pending work items, task state breakdown | ✓ |
| [deadlock-detection](memory/Threads-Concurrency/deadlock-detection.md) | Sync-block graph analysis; cycle detection for monitor-based deadlocks | ✓ |
| [async-stacks](memory/Threads-Concurrency/async-stacks.md) | Active async state machines; suspension state breakdown | ✓ |
| [native-interop](memory/Threads-Concurrency/native-interop.md) | Threads blocked in native / P/Invoke / CLR interop transition frames | ✓ |

### Infrastructure and Network

| Command | Description | In `--full` |
|---|---|:---:|
| [connection-pool](memory/Infrastructure-Network/connection-pool.md) | Live DB connections and commands by state; masked connection strings | ✓ |
| [http-requests](memory/Infrastructure-Network/http-requests.md) | In-flight `HttpRequestMessage` / `HttpClient` objects by method and URL | ✓ |
| [timer-leaks](memory/Infrastructure-Network/timer-leaks.md) | Live `System.Threading.Timer` instances; due-time, period, and callback | ✓ |
| [wcf-channels](memory/Infrastructure-Network/wcf-channels.md) | WCF channel state breakdown; endpoint addresses and fault reasons | ✓ |

### Targeted and Interactive

| Command | Description | In `--full` |
|---|---|:---:|
| [type-instances](memory/Targeted-Interactive/type-instances.md) | All instances of a specific type with individual sizes and retained sizes | — |
| [object-inspect](memory/Targeted-Interactive/object-inspect.md) | Deep recursive field dump for one object by address | — |
| [module-list](memory/Targeted-Interactive/module-list.md) | Loaded assemblies classified as Dynamic, GAC, System, or App | ✓ |
| [gc-roots](memory/Heap-Memory/gc-roots.md) | Trace root-holding paths from a specific type or object address | — |

---

## Trace Commands

Commands that operate on `.nettrace` or `.etl` trace files.

### Combined / Cross-source

| Command | Description |
|---|---|
| [trace-analyze](trace/Orchestrator/trace-analyze.md) | Full trace report — opens trace once and runs all 29 sub-analyzers in a single pass |
| [trace-dump-analyze](trace/Orchestrator/trace-dump-analyze.md) | Cross-source analysis — runs all 29 trace sub-analyzers + lightweight dump walk + correlation engine |

### CPU and Allocation

| Command | Description |
|---|---|
| [cpu-trace](trace/CPU-Allocation/cpu-trace.md) | CPU sampling: hot path, top methods by exclusive time, call tree |
| [alloc-trace](trace/CPU-Allocation/alloc-trace.md) | `GCAllocationTick` sampling: top types and call sites by estimated byte volume |
| [alloc-burst-trace](trace/CPU-Allocation/alloc-burst-trace.md) | Identify 500 ms windows with 3× or more the median allocation rate |

### GC and Memory

| Command | Description |
|---|---|
| [gc-trace](trace/GC-Exceptions-Locks/gc-trace.md) | GC Start/Stop pairs: per-generation pause stats, trigger reasons, heap sizes |
| [finalizer-trace](trace/GC-Exceptions-Locks/finalizer-trace.md) | Finalization bursts, queue growth, and top finalizer types from GC events |
| [loh-trace](trace/GC-Exceptions-Locks/loh-trace.md) | LOH growth across GC collections; fragmentation trend detection |

### Exceptions and Locks

| Command | Description |
|---|---|
| [contention-trace](trace/GC-Exceptions-Locks/contention-trace.md) | `ContentionStart/Stop` pairs: lock hotspots by total wait time |
| [exceptions-trace](trace/GC-Exceptions-Locks/exceptions-trace.md) | First-chance exception events: top types, flood detection, throw sites |
| [deadlock-trace](trace/Threads-Concurrency/deadlock-trace.md) | Heuristic detection of mutually-blocked thread pairs from contention and wait events |

### Threads and Async

| Command | Description |
|---|---|
| [threadpool-starvation](trace/Threads-Concurrency/threadpool-starvation.md) | WaitHandle events and hill-climbing adjustments: starvation signal detection |
| [async-trace](trace/Threads-Concurrency/async-trace.md) | Async Task scheduling, sync-over-async hotspots, continuation call sites |
| [context-switch-trace](trace/Threads-Concurrency/context-switch-trace.md) | Thread scheduling frequency, voluntary vs. preempted splits, wait-reason breakdown (ETL only) |
| [task-scheduler-trace](trace/Threads-Concurrency/task-scheduler-trace.md) | Long-running tasks, cancelled tasks, and excessive wait depth from Task events |

### JIT

| Command | Description |
|---|---|
| [jit-trace](trace/JIT-HTTP/jit-trace.md) | JIT compilation time, slowest methods, top modules by compilation load |

### HTTP and ASP.NET

| Command | Description |
|---|---|
| [http-trace](trace/JIT-HTTP/http-trace.md) | HTTP request latency, top endpoints by count and latency, error rates |
| [kestrel-trace](trace/JIT-HTTP/kestrel-trace.md) | Kestrel connection rejections, queue pressure, and request errors |
| [aspnetcore-pipeline-trace](trace/JIT-HTTP/aspnetcore-pipeline-trace.md) | Auth failures, unmatched routes, and endpoint error patterns from ASP.NET Core events |

### SQL and Serialization

| Command | Description |
|---|---|
| [sql-trace](trace/SQL-Serialization/sql-trace.md) | SQL/EF query latency, slow query list, per-database summary |
| [json-trace](trace/SQL-Serialization/json-trace.md) | CPU time and allocation pressure from `System.Text.Json`, Newtonsoft, and DataContract JSON |
| [connection-pool-trace](trace/SQL-Serialization/connection-pool-trace.md) | DB connection open/close tracking, leak detection, and peak concurrency from SqlClient events |

### Network and I/O

| Command | Description |
|---|---|
| [socket-trace](trace/Network-IO/socket-trace.md) | Connection latency, failures, and top remote hosts from `System.Net.Sockets` events |
| [dns-trace](trace/Network-IO/dns-trace.md) | DNS lookup latency, failure storms, and top hostnames from `System.Net.NameResolution` events |
| [file-io-trace](trace/Network-IO/file-io-trace.md) | Slow synchronous reads/writes and high-throughput files from kernel file events (ETL only) |
| [handle-leak-trace](trace/Network-IO/handle-leak-trace.md) | GCHandle leak detection by comparing created vs. destroyed handles per type |

### Observability

| Command | Description |
|---|---|
| [otel-trace](trace/Observability/otel-trace.md) | OpenTelemetry Activity span latency, error rates, and top operations from DiagnosticSource events |
| [process-lifecycle-trace](trace/Observability/process-lifecycle-trace.md) | Process crashes, restarts, and abnormal exits from Process/Start/Stop events |
| [retry-storm-trace](trace/Observability/retry-storm-trace.md) | Bursts of transient retry-pattern exceptions; identifies retry-storm periods |

### Intelligence

| Command | Description |
|---|---|
| [anomaly-trace](trace/Intelligence/anomaly-trace.md) | Z-score analysis over CPU, GC, allocation, contention, and exception rate timelines |
| [root-cause-trace](trace/Intelligence/root-cause-trace.md) | Runs all trace analyzers and derives ranked causal chains with actionable remediation advice |

---

## Web Performance Commands

Commands that operate on Chrome DevTools performance traces (`.json` / `.json.gz` —
the "Enhanced Trace" export from the DevTools Performance panel). Unrelated to
`.nettrace`/`.etl`: these analyze browser/JS performance (JS heap, DOM, V8 CPU
profiler, compositor frames, network), not the .NET runtime. See
[Docs/WebTrace-Plan.md](WebTrace-Plan.md) for the full design (streaming parser, cache
layout, source-map de-minification, plugin extension point).

| Command | Description |
|---|---|
| `web-analyze` | Runs every command below in one pass — 0–100 health score, ranked Action Queue (Now/Next/Watch), "Look Here First" pointer, auto-play filmstrip (HTML), `--with-plugins`, `--fail-on critical\|warning` for CI gating |
| `web-memory-leak` | JS heap / DOM node / event-listener trend; flags listener-leak (listener:node ratio) and sustained growth |
| `web-cpu-hotspots` | Main-thread CPU self-time ranked by (file, function, line), de-minified via the trace's embedded source maps when available. Each row's full caller chain is rendered inline as a `.NET`-stack-trace-style **Call Stack** (root-first, ★ marks where application code enters it); vendor hotspots reached only via a promise/deferred callback the profiler can't trace synchronously fall back to a "your code seen running nearby" timing correlation instead of showing nothing |
| `web-long-tasks` | Main-thread tasks ≥50ms (Long Tasks API threshold), ranked by duration. Root Causes are rolled up by attributed function with the same merged Call Stack treatment as `web-cpu-hotspots` |
| `web-gc-pressure` | V8 major/minor GC cycle counts and pause durations |
| `web-network` | Completed network requests (correlated Send/Receive/Finish), ranked by duration; flags slow/failed requests |
| `web-jank` | Compositor frame-drop rate (BeginFrame vs. DroppedFrame) |
| `web-input-latency` | Interaction-to-response latency (INP-style), from the trace's async `InputLatency::*` events. Each of the worst 20 interactions is identified by kind (MouseDown, GestureScrollUpdate, ...) and, when a long task overlapped its start, which function was blocking the main thread at the time — cross-referenced against `web-long-tasks` automatically |

Collecting a trace: Chrome DevTools → Performance panel → check "Memory" if you want
heap/listener trend data → Record → stop → Export (the exported `.json` can be gzip'd
or passed as-is).

---

## Triage Playbooks

### Memory Growth / Suspected Leak

1. `analyze app.dmp --full` — start here; read the health score and Critical findings
2. `memory-leak` — rank suspects by retained size and instance count
3. `high-refs` — find objects referenced by unusually many others
4. `cache-patterns` — check for unbounded Dictionary or MemoryCache instances
5. `closure-capture` — check for closures capturing large object graphs
6. `heap-fragmentation` — check if LOH fragmentation is contributing
7. `type-instances --type <SuspectType>` — enumerate individual instances
8. `object-inspect --address <addr>` — trace field values for a specific instance
9. Save `.bin` snapshots over time → `diff` to confirm growth rate

### Deadlock / Thread Hang

1. `analyze app.dmp` — check for Critical: deadlock cycle finding
2. `deadlock-detection` — get the exact thread cycle
3. `thread-analysis --blocked-only` — see all blocked threads with stack frames
4. `native-interop` — check for threads stuck in P/Invoke or CLR interop frames
5. `async-stacks` — look for async state machines stuck in `Awaiting` state
6. `thread-pool` — check if the pool is saturated

### GC Pause / Allocation Pressure

1. `gc-trace app.nettrace` — identify pause outliers and trigger reasons
2. `alloc-trace app.nettrace` — find the allocating types and call sites
3. `alloc-burst-trace app.nettrace` — identify spike windows
4. `gen-summary app.dmp` — verify Gen2/LOH growth
5. `large-objects app.dmp` — identify LOH contributors
6. `heap-fragmentation app.dmp` — measure fragmentation in LOH segments
7. `loh-trace app.nettrace` — correlate LOH growth across GC collections

### CPU Hotspot

1. `cpu-trace app.nettrace` — follow the hot path to the business-logic frame
2. `alloc-trace app.nettrace` — correlate CPU with allocation churn
3. `contention-trace app.nettrace` — check if lock waiting contributes to CPU time
4. `anomaly-trace app.nettrace` — z-score analysis to pinpoint anomaly window

### Exception Storm

1. `exceptions-trace app.nettrace` — count total exceptions and top types
2. `retry-storm-trace app.nettrace` — detect retry-pattern bursts
3. `exception-analysis app.dmp` — cross-reference with live exception instances in the dump
4. If `TaskCanceledException` / `TimeoutException` dominant: check `connection-pool` and `thread-pool`

### ThreadPool Starvation

1. `threadpool-starvation app.nettrace` — count starvation adjustment events
2. `thread-pool app.dmp` — check pending work item count
3. `async-stacks app.dmp` — count `Awaiting` async state machines
4. `task-scheduler-trace app.nettrace` — look for long-running tasks blocking the pool

### Unknown / Composite

1. `trace-dump-analyze app.nettrace app.dmp` — cross-source analysis with ranked root causes
2. `root-cause-trace app.nettrace` — trace-only causal chain synthesis
3. `anomaly-trace app.nettrace` — statistical anomaly window identification
4. `diagnose app.dmp` — multi-analyzer synthesis with executive + engineering summary
4. `deadlock-detection app.dmp` — rule out sync-over-async deadlock

---

## Output Formats

All commands accept `-o, --output <file>`. The format is inferred from the file extension:

| Extension | Format |
|---|---|
| `.html` | Self-contained HTML with inline CSS/JS, sticky navigation, collapsible sections |
| `.md` | Markdown (tables, headers, alerts as blockquotes) |
| `.txt` | Plain text with box-drawing separators |
| `.json` | Structured JSON (AOT source-gen, safe for programmatic consumption) |
| `.bin` | Brotli-compressed JSON (compact archival; replayable via `render`) |

If `-o` is omitted, a `.html` file is written alongside the dump.

---

## Environment Variables

| Variable | Effect |
|---|---|
| `DD_DUMP` | Default dump path; used when no positional `.dmp`/`.mdmp` argument is given |
| `DD_TEST_DUMP` | Test dump path for integration tests (skips tests if not set) |

---

## Configuration: `dd-thresholds.json`

Optional file in the working directory. Controls health-scoring thresholds used by `analyze` and `trend-analysis`. If absent or invalid, built-in defaults apply silently.

Key threshold groups: heap size per generation, finalizer queue depth, thread counts, event subscriber totals, connection counts, async backlog depth.

See [Memory-Guide.md → Health Scoring](Memory-Guide.md#health-scoring) for default threshold values.

---

## Health Score

The `analyze` command produces a score from **0–100** for the dump, deducting points for each finding:

| Signal | Deduction |
|---|---|
| Event leak > 1000 subscribers on a single field | -20 |
| Thread pool saturated | -15 |
| Heap > 2 GB | -15 |
| Finalizer queue > 500 objects | -15 |
| Async backlog > 500 continuations | -10 |
| Heap fragmentation >= 40% | -10 |
| DB connections > 50 | -10 |
| LOH > 500 MB | -10 |
| WCF faulted channels | -10 |
| Event leaks (moderate) | -10 |
| Blocked threads > 20 | -10 |
| Heap fragmentation 20–40% | -5 |
| Blocked threads 5–20 | -5 |
| Finalizer queue 100–500 | -5 |
| Async backlog 100–500 | -5 |
| Thread pool near capacity | -5 |
| Exception threads > 5 | -5 |
| String duplication > 100 MB | -5 |
| Pinned handles > 2000 | -5 |
| Timer objects > 500 | -5 |

Score labels: **Healthy** (≥85) · **Stable** (≥70) · **Degraded** (≥50) · **Critical** (<50)

Thresholds are fully configurable via `dd-thresholds.json` placed alongside the executable.

---

## Performance and Resource Expectations

Analysis time and memory scale with **object count**, not dump file size. A largely native-memory process can produce a multi-GB dump with very few managed objects and complete in seconds.

### Combined estimates

| Dump file size | Typical object count | `analyze --full` wall clock | Peak working set |
|---|---|---|---|
| < 500 MB | < 1 M | < 5 s | < 300 MB |
| 500 MB – 4 GB | 1 – 15 M | 10–30 s | < 2 GB |
| 4 – 15 GB | ~15 – 50 M | 1–3 min | 2–6 GB |
| 15 – 30 GB | ~50 – 120 M | 5–8 min (with cache) / 15–25 min (first run) | 12–17 GB |

> Use `--debug` on a first run to print peak working set and the exact object count.

### Full-analyze benchmark (~25 GB IIS dump, 86.5 M objects)

| Stage | Time |
|---|---|
| Heap walk | 132.6 s |
| Finalizer queue scan | 22.2 s |
| BFS index build (first run, 3 passes) | ~352 s |
| BFS index load (cached) | 16.3 s |
| Dominator index build (first run, 3 passes) | ~390 s |
| Dominator index load (cached) | ~2 s |
| Sub-reports wall time (30 in parallel) | ~30–240 s depending on cache state |

### BFS index build timings

| Phase | ~87 M nodes | ~95 M nodes | ~111 M nodes |
|---|---:|---:|---:|
| Pass 1 — enumerate | 47.6 s | 48.0 s | 52.6 s |
| Pass 2 — count edges | 66.5 s | 68.0 s | 74.0 s |
| Pass 3 — fill edges | 67.1 s | 68.7 s | 74.0 s |
| Save (Brotli Optimal) | 30.7 s | 32.4 s | 36.6 s |
| **Total build** | **211.9 s** | **217.1 s** | **237.2 s** |
| Load (subsequent runs) | 9.8 s | 10.2 s | 11.5 s |

### Hardware recommendations

**Minimum (< 4 GB dumps)**

| Component | Minimum |
|---|---|
| RAM | 4 GB free |
| Storage | SSD required — dump is memory-mapped with random I/O |
| CPU | 4 physical cores (8 logical) |

**Recommended (4–30 GB production dumps)**

| Component | Recommended |
|---|---|
| RAM | 16 GB free minimum; 20 GB preferred for `analyze --full` on 25 GB+ dumps |
| Storage | NVMe SSD |
| CPU | 8 physical cores (16 logical) |

---

## Project Structure

```
DumpDetective.slnx

DumpDetective.Core/               Models, interfaces, shared utilities
  Interfaces/
    ICommand.cs                   Name, Description, IncludeInFullAnalyze, Category, Kind, Run, BuildReport
    IRenderSink.cs                Format-agnostic output interface
    IHeapObjectConsumer.cs        Heap-walk consumer interface
    ITracePlugin.cs               Trace sub-analyzer interface for plugins
  Models/
    DumpSnapshot.cs               All collected metrics for one dump (JSON-serialisable)
    Finding.cs                    Scored finding (severity, category, headline, advice)
    ReportDoc.cs                  Replayable report document model
    ThresholdConfig.cs            Configurable scoring / trend thresholds
  Runtime/
    DumpContext.cs                ClrMD DataTarget + ClrRuntime wrapper
    HeapSnapshot.cs               TypeStats, InboundCounts, StringGroups, gen counters
  Utilities/
    CliArgs.cs                    Shared argument parser
    CommandBase.cs                Execute lifecycle, TryHelp, RunStatus
    DumpHelpers.cs                FormatSize, IsSystemType, OpenDump, SegmentKindLabel
    HealthScorer.cs               Score(DumpSnapshot, ScoringThresholds) -> (Findings, score)
    ProgressLogger.cs             Live spinner + completion lines via Spectre.Console

DumpDetective.Analysis.Memory/    ClrMD data collection and heap walking
  DumpCollector.cs                CollectFull / CollectLightweight orchestration
  HeapWalker.cs                   Single EnumerateObjects() call feeding all consumers
  BfsIndexBuilder.cs              3-pass parallel CSR graph builder for .bfs.idx cache
  BfsIndexCache.cs                Load / validate / save the .bfs.idx cache
  LengauerTarjan.cs               Iterative LT dominator algorithm with path compression
  DomTreeBuilder.cs               3-pass dominator-index builder (BuildGraph/RunLT/FinalizeAndSave)
  DomTreeCache.cs                 Load / validate / save the .idom.idx cache
  Consumers/                      IHeapObjectConsumer implementations (one concern each)
  Analyzers/                      Per-command analysis logic (pure POCO in / POCO out)

DumpDetective.Analysis.Trace/     .nettrace / ETL data collection
  Analyzers/                      One file per trace command

DumpDetective.Analysis.WebTrace/  Chrome DevTools performance trace (.json/.json.gz) collection
  Parsing/
    ChromeTraceParser.cs          Single-pass streaming parser — Utf8JsonReader over a buffered
                                   cursor, no whole-file DOM; classifies/aggregates as it scans
    JsonBufferCursor.cs           Bounded-buffer reader feeding the streaming Utf8JsonReader
    SourceMapVlq.cs               VLQ decoder for embedded source maps (de-minifies file:line)
  Analysis/                       One analyzer per web-* command (pure POCO in / POCO out)
  Cache/
    WebTraceCache.cs              Versioned binary cache of the parsed WebTraceData (keyed by
                                   source file size/timestamp — any format change bumps Version)
  Model/
    WebTraceModels.cs             WebTraceData and all raw per-event record types
  WebTraceContext.cs              Load-or-parse-and-cache entry point used by every web-* command

DumpDetective.Reporting/          Output format implementations
  Sinks/
    HtmlSink.cs                   Self-contained HTML; inline CSS/JS; sticky nav
    MarkdownSink.cs / TextSink.cs / JsonSink.cs / BinSink.cs / CaptureSink.cs
  Reports/                        Per-command report builders
    WebCallChainHelper.cs         Shared "call stack" formatting (Core.Models.CallTreeNode
                                   builder + .NET-stack-trace-style text) used by every
                                   web-* report that has a resolved caller chain
  ReportDocReplay.cs              Replays a ReportDoc through any IRenderSink
  ReportDiffer.cs                 Produces diff ReportDoc from two inputs

DumpDetective.Commands/           ICommand implementations
  Memory/                         Memory-dump commands
  Trace/                          .nettrace / ETL commands
  Web/                            Chrome DevTools trace commands (web-*)

DumpDetective.Cli/                Entry point
  Program.cs                      Top-level statements; --debug flag
  CommandRegistry.cs              Single source of truth for dump/trace ICommand instances
  WebCommandRegistry.cs           Single source of truth for web-* ICommand instances
  HelpPrinter.cs                  Dynamic --help grouped by ICommand.Category

DumpDetective.DiagnosticScenarios/  Per-scenario dump generation for tests
DumpDetective.Tests/              xUnit test project
```

### Dependency graph

```
Cli ──────────────────────────────────────► Commands
 │                                              │
 │                                              ▼
 │                               Analysis.Memory ──────┐
 │                               Analysis.Trace  ──────┤
 │                               Analysis.WebTrace ────┤
 │                                                     │
 └──────────────────► Reporting ──────────► Core ◄─────┘
```
