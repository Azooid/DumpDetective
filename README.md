# DumpDetective

A .NET CLI tool for diagnosing production incidents from Windows memory dumps and .NET traces.

Point it at a `.dmp` or `.nettrace` file and get an HTML report with a health score, prioritized findings, and 30+ targeted sub-reports — heap analysis, memory leak detection, thread and deadlock inspection, CPU hot paths, GC diagnostics, and more.

---

## Install

``bash
dotnet tool install --global DumpDetective.Cli
``

``bash
dotnet tool update --global DumpDetective.Cli    # update
dotnet tool uninstall --global DumpDetective.Cli  # remove
``

---

## What it does

- **Health score** (0–100) with prioritized Critical / Warning / Info findings per dump.
- **30 memory sub-reports** run in parallel from a single heap walk: heap stats, memory leak suspects with GC root traces, Lengauer-Tarjan dominator tree, fragmentation, pinned objects, static refs, event handler leaks, deadlocks, async backlogs, thread pool pressure, and more.
- **29 trace sub-reports** from one trace-file pass: CPU hot paths, allocation hotspots, GC pauses, contention, exceptions, ThreadPool starvation, async tasks, JIT, HTTP, SQL, network I/O, OpenTelemetry, and more.
- **Cross-source correlation** (`trace-dump-analyze`) links trace signals to heap evidence automatically.
- **Multi-dump trend analysis** for comparing behavior across time or deployments.
- **Report replay** — save any report as `.bin` (Brotli-compressed) and convert to any format later without re-opening the dump.
- **Report diff** — compare two saved reports side-by-side; changed cells, new alerts, and per-section deltas.
- **Plugin system** — drop a `.NET` class library into `plugins/` to add custom commands without modifying the host binary.
- **BFS retained-size index** (`.bfs.idx`) built once per dump, reused on every subsequent run.

---

## Requirements

- .NET 8+ runtime (for `dotnet tool install`); .NET 10 SDK required to build from source
- Windows (WinDbg-style dumps); Linux `.core` dumps supported from v3.3.0
- 4 GB+ free RAM for small dumps; 16–20 GB recommended for large production dumps (> 15 GB)
- SSD required — dumps are memory-mapped with random I/O patterns

---

## Quick start

``bash
# Full scored report — heap walk + 30 sub-reports in parallel
DumpDetective analyze app.dmp --full

# Save as replayable binary (re-render to any format without reopening the dump)
DumpDetective analyze app.dmp --full --output report.bin
DumpDetective render report.bin --output report.html

# Pre-build all analysis caches once (subsequent analyze runs are much faster)
DumpDetective load app.dmp

# Combined trace analysis
DumpDetective trace-analyze app.nettrace

# Cross-source trace + dump analysis
DumpDetective trace-dump-analyze app.nettrace app.dmp --output incident.html

# Compare two saved reports
DumpDetective diff before.bin after.bin --output delta.html
``

---

## Memory dump commands

| Category | Commands |
|---|---|
| Orchestration | `analyze`, `trend-analysis` |
| Replay / diff | `render`, `diff` |
| Cache lifecycle | `load`, `close` |
| Heap overview | `heap-stats`, `gen-summary`, `memory-pressure`, `heap-fragmentation`, `large-objects`, `pinned-objects` |
| Memory leaks | `memory-leak`, `high-refs`, `string-duplicates`, `dominator-tree` |
| Retention signals | `cache-patterns`, `closure-capture`, `datatable-amp`, `static-refs`, `weak-refs`, `event-analysis`, `gc-root-map` |
| GC / lifetime | `finalizer-queue`, `handle-table` |
| Threads | `thread-analysis`, `thread-pool`, `deadlock-detection`, `async-stacks`, `native-interop` |
| Infrastructure | `connection-pool`, `http-requests`, `timer-leaks`, `wcf-channels`, `module-list` |
| Targeted | `type-instances`, `object-inspect`, `gc-roots` |

---

## Trace commands

| Category | Commands |
|---|---|
| Combined | `trace-analyze`, `trace-dump-analyze` |
| CPU / alloc | `cpu-trace`, `alloc-trace`, `alloc-burst-trace` |
| GC / memory | `gc-trace`, `finalizer-trace`, `loh-trace` |
| Exceptions / locks | `contention-trace`, `exceptions-trace`, `deadlock-trace` |
| Threads / async | `threadpool-starvation`, `async-trace`, `context-switch-trace`, `task-scheduler-trace` |
| JIT | `jit-trace` |
| HTTP / ASP.NET | `http-trace`, `kestrel-trace`, `aspnetcore-pipeline-trace` |
| SQL / data | `sql-trace`, `json-trace`, `connection-pool-trace` |
| Network / I/O | `socket-trace`, `dns-trace`, `file-io-trace`, `handle-leak-trace` |
| Observability | `otel-trace`, `process-lifecycle-trace`, `retry-storm-trace` |
| Intelligence | `anomaly-trace`, `root-cause-trace` |

---

## Output formats

Every command writes `<dump-name>.html` by default. Use `-o` / `--output` or `--format` to change this. Both flags are repeatable:

``bash
DumpDetective heap-stats app.dmp -o report.html -o report.bin  # two files at once
DumpDetective analyze app.dmp --full --format bin               # auto-named .bin
``

| Extension | Description |
|---|---|
| `.html` | Interactive — sticky nav, charts, sortable tables, dark mode, self-contained |
| `.md` | Markdown |
| `.json` | Structured JSON, re-renderable via `render` |
| `.bin` | Brotli-compressed JSON (~50–70% smaller than `.json`) |
| `.txt` | Plain text |

---

## Documentation

Full command reference, options, triage playbooks, performance benchmarks, and architecture details:

- **[Docs/documentation.md](Docs/documentation.md)** — all commands, output formats, performance expectations, health score, project structure
- [Memory Analysis Guide](Docs/Memory-Guide.md) — dump workflows and all memory command options
- [Trace Analysis Guide](Docs/Trace-Guide.md) — trace workflows and all trace command options
- [Cache Inventory](Docs/cache.md) — BFS index, `.ddcache`, and cache lifecycle
- [Plugin System](Docs/Plugins.md) — write and install custom analysis commands
- [Architecture](Docs/Architecture.md) — project structure, dependency graph, and extension points

---

## License

[MIT](LICENSE)
