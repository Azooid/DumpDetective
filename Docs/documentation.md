# DumpDetective Documentation

DumpDetective is a .NET AOT CLI tool for analyzing Windows memory dumps (`.dmp` / `.mdmp`) and performance traces (`.nettrace` / `.etl`). It runs commands for heap statistics, memory leak detection, deadlock diagnosis, GC analysis, CPU profiling, allocation hotspots, and more — producing output as HTML, Markdown, JSON, or plain text.

---

## Quick Start

```bash
# Full incident report from a dump
DumpDetective analyze app.dmp --full --output report.html

# Combined trace analysis
DumpDetective trace-analyze app.nettrace --output trace.html

# Re-render a saved report in a different format
DumpDetective render report.bin --output report.md
```

---

## Documentation Sections

| Section | Contents |
|---|---|
| [Memory Analysis Guide](Memory-Guide.md) | Workflows, triage paths, and all memory command options |
| [Trace Analysis Guide](Trace-Guide.md) | Workflows and all trace command options |
| [Cache Inventory](cache.md) | Built-in system caches, storage locations, lifecycle, and cleanup |
| [Plugin System](Plugins.md) | How to write and install external plugin commands |
| [Memory Commands](#memory-commands) | All 31 dump-analysis commands with links to detailed docs |
| [Trace Commands](#trace-commands) | All 12 trace commands with links to detailed docs |

---

## Memory Commands

Commands that operate on `.dmp` / `.mdmp` dump files.

### Orchestration

| Command | Description | In `--full` |
|---|---|:---:|
| [analyze](memory/Orchestrator/analyze.md) | Scored health report for one dump; optionally runs all sub-commands | — |
| [trend-analysis](memory/Orchestrator/trend-analysis.md) | Multi-dump trend report: heap growth, type counts, GC metrics across captures | — |

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

### Heap and Memory

| Command | Description | In `--full` |
|---|---|:---:|
| [heap-stats](memory/Heap-Memory/heap-stats.md) | Top types by total size and instance count across the entire heap | ✓ |
| [gen-summary](memory/Heap-Memory/gen-summary.md) | Heap bytes and object counts broken down by GC generation (Gen0–POH) | ✓ |
| [heap-fragmentation](memory/Heap-Memory/heap-fragmentation.md) | Free-hole analysis per heap segment; live/free byte ratio per segment | ✓ |
| [large-objects](memory/Heap-Memory/large-objects.md) | Individual objects ≥ 85 KB on the LOH; aggregated by type | ✓ |
| [pinned-objects](memory/Heap-Memory/pinned-objects.md) | Objects pinned via GC handles; grouped by type and handle kind | ✓ |
| [memory-leak](memory/Heap-Memory/memory-leak.md) | Suspects ranked by count and size; root-chain traces to GC roots | ✓ |
| [high-refs](memory/Heap-Memory/high-refs.md) | Most-referenced objects by inbound reference count | ✓ |
| [string-duplicates](memory/Heap-Memory/string-duplicates.md) | Duplicate string groups sorted by wasted bytes | ✓ |
| [finalizer-queue](memory/Heap-Memory/finalizer-queue.md) | Types queued for finalization; resurrection detection | ✓ |
| [handle-table](memory/Heap-Memory/handle-table.md) | GC handle table breakdown by kind: Strong, WeakShort, Pinned, Async, Dependent | ✓ |
| [static-refs](memory/Heap-Memory/static-refs.md) | Statically-rooted object trees; optional BFS retained-size computation | ✓ |
| [weak-refs](memory/Heap-Memory/weak-refs.md) | Weak GC handles — alive vs. collected object breakdown | ✓ |
| [event-analysis](memory/Heap-Memory/event-analysis.md) | Event fields with high subscriber counts; potential event-handler leaks | ✓ |
| [gc-roots](memory/Heap-Memory/gc-roots.md) | Trace root-holding paths from a specific type or object address | — |

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

---

## Trace Commands

Commands that operate on `.nettrace` or `.etl` trace files.

### Combined / Cross-source

| Command | Description |
|---|---|
| [trace-analyze](trace/Orchestrator/trace-analyze.md) | Combined report: runs all ten trace analyzers in a single pass |
| [trace-dump-analyze](trace/Orchestrator/trace-dump-analyze.md) | Cross-source analysis: runs all trace analyzers + lightweight dump walk, then correlates both sources to surface the highest-confidence root causes |

### CPU and Allocation

| Command | Description |
|---|---|
| [cpu-trace](trace/CPU-Allocation/cpu-trace.md) | CPU sampling: hot path, top methods by exclusive time, call tree |
| [alloc-trace](trace/CPU-Allocation/alloc-trace.md) | GCAllocationTick sampling: top types and call sites by estimated byte volume |

### GC, Exceptions, and Locks

| Command | Description |
|---|---|
| [gc-trace](trace/GC-Exceptions-Locks/gc-trace.md) | GC Start/Stop pairs: per-generation pause stats, trigger reasons, heap sizes |
| [contention-trace](trace/GC-Exceptions-Locks/contention-trace.md) | ContentionStart/Stop pairs: lock hotspots by total wait time |
| [exceptions-trace](trace/GC-Exceptions-Locks/exceptions-trace.md) | First-chance exception events: top types, flood detection, throw sites |

### Threads and Concurrency

| Command | Description |
|---|---|
| [threadpool-starvation](trace/Threads-Concurrency/threadpool-starvation.md) | WaitHandle wait events and hill-climbing adjustments: starvation signal detection |
| [async-trace](trace/Threads-Concurrency/async-trace.md) | Async Task scheduling, sync-over-async hotspots, continuation call sites |

### JIT, HTTP, and SQL

| Command | Description |
|---|---|
| [jit-trace](trace/JIT-HTTP/jit-trace.md) | JIT compilation time, slowest methods, top modules by compilation load |
| [http-trace](trace/JIT-HTTP/http-trace.md) | HTTP request latency, top endpoints by count and latency, error rates |
| [sql-trace](trace/JIT-HTTP/sql-trace.md) | SQL/EF query latency, slow query list, per-database summary |

---

## Triage Playbooks

### Memory Growth / Suspected Leak

1. `analyze app.dmp --full` — start here; read the health score and Critical findings
2. `memory-leak` — rank suspects by retained size and instance count
3. `high-refs` — find objects referenced by unusually many others
4. `heap-fragmentation` — check if LOH fragmentation is contributing
5. `type-instances --type <SuspectType>` — enumerate individual instances
6. `object-inspect --address <addr>` — trace field values for a specific instance
7. Save `.bin` snapshots over time → `diff` to confirm growth rate

### Deadlock / Thread Hang

1. `analyze app.dmp` — check for Critical: deadlock cycle finding
2. `deadlock-detection` — get the exact thread cycle
3. `thread-analysis --blocked-only` — see all blocked threads with stack frames
4. `async-stacks` — look for async state machines stuck in `Awaiting` state
5. `thread-pool` — check if the pool is saturated

### GC Pause / Allocation Pressure

1. `gc-trace app.nettrace` — identify pause outliers and trigger reasons
2. `alloc-trace app.nettrace` — find the allocating types and call sites
3. `gen-summary app.dmp` — verify Gen2/LOH growth
4. `large-objects app.dmp` — identify LOH contributors
5. `heap-fragmentation app.dmp` — measure fragmentation in LOH segments

### CPU Hotspot

1. `cpu-trace app.nettrace` — follow the hot path to the business-logic frame
2. `alloc-trace app.nettrace` — correlate CPU with allocation churn
3. `contention-trace app.nettrace` — check if lock waiting contributes to CPU time

### Exception Storm

1. `exceptions-trace app.nettrace` — count total exceptions and top types
2. `exception-analysis app.dmp` — cross-reference with live exception instances in the dump
3. If `TaskCanceledException` / `TimeoutException` dominant: check `connection-pool` and `thread-pool`

### ThreadPool Starvation

1. `threadpool-starvation app.nettrace` — count starvation adjustment events
2. `thread-pool app.dmp` — check pending work item count
3. `async-stacks app.dmp` — count `Awaiting` async state machines
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
