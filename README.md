# DumpDetective

A command-line tool for understanding .NET production incidents from dumps and traces.

DumpDetective analyzes `.dmp` / `.mdmp` memory dumps and `.nettrace` / `.etl` traces, then generates human-readable reports that help you answer practical questions quickly:

- Why is memory growing?
- What is retaining objects?
- Are we seeing thread pool pressure, deadlocks, or heavy contention?
- Are exception rates, allocations, or GC pauses abnormal?

Every command writes an HTML report alongside the dump file by default. Use `--output report.bin` to save a compact structured report, then `DumpDetective render report.bin` to convert it to any format at any time without re-opening the dump.

## Features

- One-command health report (`analyze`) with a score and prioritized findings.
- Deep memory diagnostics (`memory-leak`, `high-refs`, `gc-roots`, `object-inspect`).
- Combined trace diagnostics (`trace-analyze`) plus focused trace commands (`cpu-trace`, `alloc-trace`, `gc-trace`, `contention-trace`, `exceptions-trace`, `threadpool-starvation`, `async-trace`, `jit-trace`, `http-trace`, `sql-trace`). Recent versions cache repeated trace event-name classification inside the slowest analyzers so long traces spend less time on string matching.
- Cross-source trace + dump analysis (`trace-dump-analyze`) with 10 built-in correlation rules, plus plugin-extensible rules via `--with-plugins`.
- Multi-dump trend analysis for comparing behavior over time.
- Interactive HTML reports with grouped navigation, charts, dark mode, and paged tables.
- Compiler-generated method names (`<Foo>b__N`, `<>c`, async state machines, generics) are automatically decoded to human-readable form in all stack-frame, callback, and call-tree columns.
- Export and replay support across HTML, Markdown, text, JSON, and compressed binary.
-BFS cache (`.bfs.idx`) for faster repeated retained-size analysis on large heaps.
- **Plugin system** — drop a `.NET` class library into `plugins/` or `~/.dumpdetective/plugins/` to add custom analysis commands without modifying the host binary. Plugin commands can participate in `analyze --full` and `trace-analyze --with-plugins` by implementing `ICommand.IncludeInFullAnalyze = true` and/or `ITracePlugin`.

## Start Here

If you are new, use this path:

1. Load a dump or dump folder to build the local caches: `DumpDetective load path\to\folder-or-file`.
2. Run the command you need against the dump path: `DumpDetective <command> <path>`.
3. If you're working with a trace, run the combined analyzer first: `DumpDetective trace-analyze path\to\trace.nettrace`.
4. If you want the quickest first pass on a dump, run one full report: `DumpDetective analyze app.dmp --full`.
5. Open the generated HTML and check top findings, memory-leak, and high-refs sections first.
6. If needed, zoom in with targeted commands like `object-inspect`, `gc-roots`, or other trace commands on `.nettrace` / `.etl` files.
7. When you're done, clean up cache files you no longer need with `DumpDetective close <dump-or-folder>` to free storage.

## Contents

- [Start Here](#start-here)
- [Requirements](#requirements)
- [Installation](#installation)
- [Build](#build)
- [Quick Start](#quick-start)
- [Environment Variables](#environment-variables)
- [Commands](#commands)
- [Output Formats](#output-formats)
- [Project Structure](#project-structure)
- [Health Score](#health-score)
- [Performance & Resource Expectations](#performance--resource-expectations)
- [Thresholds](#thresholds)

**Documentation:**
- [Documentation Index](Docs/documentation.md) — all commands with links to detailed docs, triage playbooks, output format reference
- [Memory Analysis Guide](Docs/Memory-Guide.md) — all dump commands, options, and workflows
- [Trace Analysis Guide](Docs/Trace-Guide.md) — all trace commands, options, and collection recipes
- [Cache Inventory](Docs/cache.md) — built-in system caches (session, `.ddcache`, BFS, ETLX), lifecycle, and cleanup
- [Plugin System](Docs/Plugins.md) — how to write and install external plugin commands
- [Testing](Docs/Testing.md)

---

## Requirements

### Software

| Requirement | Version |
|---|---|
| .NET SDK | 10.0+ |
| Target dump runtime | .NET Framework 4.x / .NET Core / .NET 5+ |
| OS | Windows (WinDbg-style dumps) |

### Hardware

Hardware requirements scale with the dump you are analysing. The numbers below are based on measured runs.

**Minimum (small dumps, < 4 GB)**

| Component | Minimum |
|---|---|
| RAM | 4 GB free |
| Storage | **SSD required** — dump is memory-mapped with random I/O patterns; HDD will be severely slow |
| CPU | **4 physical cores (8 logical)** — the heap walk uses 8 parallel workers; fewer cores will time-slice them and increase wall-clock time significantly |

**Recommended (production dumps, 4–30 GB)**

| Component | Recommended | Why |
|---|---|---|
| RAM | 16 GB free minimum, 20 GB preferred for `analyze --full --with-plugins` on ~25 GB dumps | Latest measured runs on a ~25 GB dump peaked at 11.00 GB WS (without plugins) and 11.96 GB WS (with plugins). Keeping 16+ GB free avoids paging; extra headroom is recommended for concurrent tools and larger object graphs |
| Storage | **NVMe SSD** | Random I/O across entire dump file; faster SSD = faster heap walk |
| CPU | **8 physical cores (16 logical)** | Heap walk uses 8 workers; a second concurrent walk (event-analysis or heap-fragmentation) can spin up another 8 — 16 logical cores prevents contention |

> **Rule of thumb:** free RAM should scale with both dump size and analysis mode. Lightweight or single-command runs are usually much cheaper than `analyze --full`. For ~25 GB production dumps, plan for at least 16 GB free RAM and prefer 20 GB+ when using `--with-plugins`.

> **SSD vs HDD:** ClrMD memory-maps the dump and accesses it with highly random I/O during the heap walk, BFS, and fragmentation scan. On NVMe, recent 25 GB full runs completed in ~3.4 minutes. A spinning disk can still take dramatically longer and may cause OS paging under memory pressure.

---

## Installation

Install as a global .NET tool from [NuGet.org](https://www.nuget.org/packages/DumpDetective.Cli):

```bash
dotnet tool install --global DumpDetective.Cli --version 3.2.0
```

Once installed, the tool is available as:

```bash
DumpDetective <command>
```

To update to the latest version:

```bash
dotnet tool update --global DumpDetective.Cli
```

To uninstall:

```bash
dotnet tool uninstall --global DumpDetective.Cli
```

---

## Build

```bash
dotnet build DumpDetective.Tests/DumpDetective.Tests.csproj
```

For a self-contained, AOT-compiled single executable:

```bash
dotnet publish DumpDetective.Cli -r win-x64 -c Release
```

The output is a single native binary: `DumpDetective.Cli.exe`.

### Latest Build Verification (May 2026)

| Command | Result | Notes |
|---|---|---|
| `dotnet build .\Docs\PluginExample\Example.DumpDetective\Example.DumpDetective.csproj` | Success | Plugin deployed warning is expected from the example plugin deploy target |

This build was used for the benchmark logs in [Performance & Resource Expectations](#performance--resource-expectations).

### Running Tests

```bash
dotnet test DumpDetective.Tests
```

72 integration tests cover every analysis command. Each test runs against its own isolated heap dump captured by `DumpDetective.DiagnosticScenarios`. Subsequent runs reuse cached dumps from `%TEMP%\DumpDetective\Scenarios\` and complete in under a second.

See [Docs/Testing.md](Docs/Testing.md) for the full test architecture and how to add new tests.

---

## Quick Start

Choose the path that matches your input type.

**Plugin flags:**
```bash
# Include plugin memory commands in full-analyze (IncludeInFullAnalyze = true)
DumpDetective analyze app.dmp --full --with-plugins

# Include plugin trace sub-analyzers in trace-analyze
DumpDetective trace-analyze perf.etl --with-plugins

# Both
DumpDetective trace-dump-analyze perf.etl app.dmp --with-plugins
```

### Memory Dump Quick Start (`.dmp`, `.mdmp`)

1. Run one full report: `DumpDetective analyze app.dmp --full`
2. Open the generated HTML report.
3. Use targeted dump commands only where needed.

```bash
# Optional: set a default dump path
$env:DD_DUMP = "C:\dumps\w3wp.dmp"

# Fast first run
DumpDetective analyze app.dmp --full

# Include peak memory diagnostics
DumpDetective analyze app.dmp --full --debug

# Save as .bin for replay later (recommended)
DumpDetective analyze app.dmp --full --output report.bin
DumpDetective render report.bin

# Write both HTML and .bin in one pass
DumpDetective analyze app.dmp --full -o report.html -o report.bin

# Run a focused dump command
DumpDetective object-inspect app.dmp -x 0x00000276DB084170 --retained

# Trend across multiple dumps
DumpDetective trend-analysis d1.dmp d2.dmp d3.dmp --full --output week1.bin
DumpDetective trend-analysis d4.dmp d5.dmp d6.dmp --full --output week2.bin
DumpDetective diff week1.bin week2.bin -o delta.html
```

### .NET Trace Quick Start (`.nettrace`, `.etl`)

1. Start with a combined trace report: `DumpDetective trace-analyze app.nettrace`
2. Open the HTML report to identify hotspots.
3. Run a single trace command for deeper drill-down.

```bash
# Combined trace analysis (all 10 analyzers)
DumpDetective trace-analyze app.nettrace

# Cross-source analysis using both trace and dump
DumpDetective trace-dump-analyze app.nettrace app.dmp --output incident.html

# Focused trace commands
DumpDetective cpu-trace app.nettrace --output cpu-report.html
DumpDetective gc-trace perf.etl --process w3wp --top 50 --output gc-report.html
DumpDetective sql-trace perf.etl --process w3wp --slow-ms 500 --output sql-report.html
DumpDetective threadpool-starvation perf.etl --top 50 --output starvation.html
```

---

## Environment Variables

| Variable | Description |
|---|---|
| `DD_DUMP` | Default dump file path. Used automatically when no `.dmp` argument is provided. |

---

## Commands

Detailed command references:

- [Documentation Index](Docs/documentation.md) — complete command tables, triage playbooks, and output format reference
- [Memory Analysis Guide](Docs/Memory-Guide.md) — all dump commands with options and examples
- [Trace Analysis Guide](Docs/Trace-Guide.md) — all trace commands with options and collection recipes

### Command Families

| Family | Commands | Input |
|---|---|---|
| Health / orchestration | `analyze`, `trend-analysis` | dump files |
| Report replay / comparison | `render`, `diff` | saved `.json` / `.bin` |
| Memory dump analysis | `heap-stats`, `gen-summary`, `memory-leak`, `gc-roots`, `object-inspect`, `load`, `close`, and related dump commands | `.dmp`, `.mdmp` |
| Trace analysis | `trace-analyze`, `trace-dump-analyze`, `cpu-trace`, `alloc-trace`, `gc-trace`, `contention-trace`, `exceptions-trace`, `threadpool-starvation`, `async-trace`, `jit-trace`, `http-trace`, `sql-trace` | `.nettrace`, `.etl` (+ `.dmp` for `trace-dump-analyze`) |

The sections below follow that same split: high-level workflows first, then dump-only commands, then trace-only commands.

### `analyze`

Scored health report for a single dump.

```
DumpDetective analyze <dump-file> [options]

Options:
  --full               Full combined report (scored summary + all sub-reports in parallel)
  --debug              Print peak working set / managed heap / private bytes at exit
  -o, --output <file>  Write report to file (.html / .md / .txt / .json / .bin)
                       Repeatable: -o report.html -o report.bin  writes both files
  --format <fmt>       Output format shorthand: html|md|json|bin
                       Repeatable: --format html --format bin  writes both files
                       Combined: -o report.html --format bin  auto-adds report.bin
  Default: writes <dump-name>.html alongside the dump
```

**What it covers:**
- Health score (0-100) with per-finding deductions
- Findings grouped as Critical / Warning / Info with recommendations
- Memory: heap by generation (SOH / LOH / POH), fragmentation
- Threads: blocked, async backlog, thread pool saturation
- Exceptions, finalizer queue, GC handles (pinned / strong / weak)
- Event handler leaks, string duplication, timer objects
- WCF channels, DB connections, top types by size

**Examples:**
```bash
DumpDetective analyze app.dmp
DumpDetective analyze app.dmp --full
DumpDetective analyze app.dmp --full --output full-report.html
DumpDetective analyze app.dmp --full --output full-report.html --format bin
DumpDetective analyze app.dmp --full --output full-report.html --debug
DumpDetective analyze app.dmp --format bin     # Brotli-compressed output
```

---

### `trend-analysis`

Cross-dump trend report comparing two or more snapshots over time.

```
DumpDetective trend-analysis <dump1> <dump2> [<dump3>...] [options]
DumpDetective trend-analysis <directory> [options]
DumpDetective trend-analysis --list <file.txt> [options]

Options:
  --full                   Full collection per dump (event leaks, string duplicates,
                           and per-dump sub-reports embedded in .json/.bin output)
  --baseline <n>           1-based index of the dump to use as the trend baseline (default: 1)
  --prefix <p>             Prefix for dump labels (default: D → D1, D2, D3).
                           E.g. --prefix W → W1, W2, W3
  --ignore-event <type>    Exclude publisher types whose name contains <type> (repeatable)
  -o, --output <f>         Write report to file (.html / .md / .txt)
                           .json  -- saves raw snapshot data (re-render any time with 'render')
                           .bin   -- saves Brotli-compressed raw snapshot data
                           Repeatable: -o trends.html -o trends.bin  writes both files
  --format <fmt>           Format shorthand: html|md|json|bin
                           Repeatable: --format html --format bin  writes both files
                           Combined: -o trends.html --format bin  auto-adds trends.bin
  Default: writes <command>.html in the current directory
```

**Report sections:**

| # | Section |
|---|---|
| 0 | Dump Timeline |
| 1 | Incident Summary -- signal status table, per-dump findings accordions, executive paragraph |
| 2 | Overall Growth Summary |
| 3 | Thread and Application Pressure |
| 4 | Event Leak Analysis |
| 5 | Finalizer Queue Detail |
| 6 | Highly Referenced Objects |
| 7 | Rooted Objects Analysis |
| 8 | Duplicate String Analysis |

**Examples:**
```bash
DumpDetective trend-analysis d1.dmp d2.dmp d3.dmp
DumpDetective trend-analysis d1.dmp d2.dmp d3.dmp --output trends.html
DumpDetective trend-analysis d1.dmp d2.dmp d3.dmp --baseline 2 --output report.html
DumpDetective trend-analysis d1.dmp d2.dmp d3.dmp --full --output snapshots.json
DumpDetective trend-analysis d1.dmp d2.dmp d3.dmp --full --output snapshots.bin  # compressed
DumpDetective trend-analysis C:\dumps\ --full --output report.html
DumpDetective trend-analysis --list dumps.txt --full --output report.md
DumpDetective trend-analysis d1.dmp d2.dmp --full --ignore-event SNINativeMethodWrapper
DumpDetective trend-analysis d1.dmp d2.dmp d3.dmp --prefix W --output week1.html
```

---

### `diff`

Compares two saved report files (`.json` or `.bin`) and produces a diff report. No dump file required.

```
DumpDetective diff <before.json|before.bin> <after.json|after.bin> [options]

Supported input formats:
  report     Produced by any single-dump command with -o *.json or -o *.bin
  trend-raw  Produced by trend-analysis -o *.json or -o *.bin

What is diffed:
  Tables       Rows matched by key column (default: col 0). Changed cells: before → after.
               Per-dump tables (Dump Timeline, Rooted Objects, etc.) are matched positionally.
  Alerts       Matched by title. Level and detail changes highlighted.
  Key-Values   Matched by key. Changed values: before → after.
  Details      Accordion blocks included from the "after" file as-is.

Options:
  --key-col <n>        Column index (0-based) used as the row key for tables (default: 0)
  --changed-only       Omit chapters/sections with no changes
  --show-same          Include unchanged rows in diff tables (default: omitted)
  --command <name>     For trend-raw: diff only this command's sub-report chapters (repeatable).
                       Dumps matched by filename; if no filenames overlap (different dump sets),
                       falls back to positional matching (Dump 1 ↔ Dump 1, etc.)
  --ignore-event <t>   For trend-raw: exclude event publisher types containing <t> (repeatable)
  -o, --output <file>  Output path (.html / .md / .txt / .json / .bin)
                       Default: <before>-vs-<after>.html
  -h, --help           Show this help
```

**Examples:**
```bash
# Single-dump report diff
DumpDetective analyze before.dmp --full -o before.bin
DumpDetective analyze after.dmp  --full -o after.bin
DumpDetective diff before.bin after.bin -o delta.html

# Trend-raw diff (week-over-week)
DumpDetective diff week1.bin week2.bin -o trend-delta.html
DumpDetective diff week1.bin week2.bin --changed-only -o delta.html

# Diff only the memory-leak sub-report across two trend files
DumpDetective diff week1.bin week2.bin --command memory-leak -o memleak-delta.html

# Multiple commands at once
DumpDetective diff week1.bin week2.bin --command memory-leak --command heap-stats -o subset.html
```

---

### `render`

Converts any DumpDetective JSON or compressed binary file to HTML, Markdown, or plain text -- **no dump file required**. (Previously also available as `trend-render`; that alias has been removed — use `render` for all file conversions.)

```
DumpDetective render <file.json|file.bin> [options]

Accepted input:
  report     JSON or .bin produced by any single-dump command with --output *.json / *.bin
  trend-raw  JSON or .bin produced by trend-analysis --output *.json / *.bin

Options:
  --baseline <n>         Trend baseline (trend-raw only; default: 1 = first dump)
  --ignore-event <type>  Filter event types (trend-raw only; repeatable)
  --mini                 Trend summary only -- suppress per-dump sub-reports even
                         when they are present in the file (trend-raw only)
  --from <n>             Extract dump #N's full sub-report as a standalone file.
                         Requires the file to have been saved with --full. 1-based.
  --command <name>       Extract only the named command's chapter(s).
                         Combine with --from to target a single dump.
                         Repeatable: --command memory-leak --command heap-stats
                         Valid names: any command that runs in analyze --full
  -o, --output <file>    Output file (.html / .md / .txt / .json / .bin)
                         Repeatable: -o report.html -o report.bin  writes both files
  --format <fmt>         Format shorthand: html|md|json|bin
                         Repeatable: --format html --format bin  writes both files
                         Combined: -o report.html --format bin  auto-adds report.bin
  Default: writes <input-name>.html
```

**Examples:**
```bash
# Default: renders to report.html
DumpDetective render report.bin
DumpDetective render snapshots.json

# Explicit output format
DumpDetective render snapshots.bin --output report.html
DumpDetective render snapshots.bin --format md

# Trend summary only (no per-dump sub-reports)
DumpDetective render snapshots.bin --mini --output trend-only.html

# Re-render at a different baseline
DumpDetective render snapshots.bin --baseline 2 --output report-d2base.html

# Extract dump #4's full sub-report as a standalone file
DumpDetective render snapshots.bin --from 4 --output d4-full.html

# Extract just the memory-leak chapter from dump #4
DumpDetective render snapshots.bin --from 4 --command memory-leak --output d4-memleak.html

# Extract memory-leak from every dump, stacked in one file
DumpDetective render snapshots.bin --command memory-leak --output all-memleak.html

# Multiple commands from dump #2
DumpDetective render snapshots.bin --from 2 --command memory-leak --command heap-stats --output d2-subset.html

# JSON is still supported when needed
DumpDetective render snapshots.json --output report.html

# Convert a single-dump report BIN / JSON to HTML
DumpDetective render heap-stats.bin
DumpDetective render heap-stats.json
```

> **Note:** `--from` and `--command` require `trend-raw` data saved with `--full` (from `.json` or `.bin`).
> If the source was saved without `--full`, sub-reports are not present and extraction will fail with a clear error message.

---

### Memory Dump Commands

Each command accepts `<dump-file>` and `--help`.
By default every command writes `<dump-name>.html` alongside the dump file. Use `--output <file>` or `--format <fmt>` to change this.
Both `-o` and `--format` are **repeatable**: `-o report.html -o report.bin` or `--format html --format bin` writes both files simultaneously. You can also mix them: `-o report.html --format bin` auto-adds `report.bin`.

| Command | Incl. in `--full` | Description |
|---|:---:|---|
| `heap-stats` | Yes | Heap object counts and sizes grouped by type |
| `gen-summary` | Yes | Object counts and sizes by GC generation |
| `heap-fragmentation` | Yes | Segment free space and fragmentation percentage |
| `large-objects` | Yes | Large objects on LOH / POH / Gen heap |
| `pinned-objects` | Yes | Pinned GC handles causing heap fragmentation |
| `memory-leak` | Yes | Suspect types with root-chain BFS traces |
| `high-refs` | Yes | Highly-referenced "hub" objects -- caches, shared state |
| `string-duplicates` | Yes | Duplicate strings and wasted memory |
| `finalizer-queue` | Yes | Objects waiting in the finalizer queue |
| `handle-table` | Yes | GC handles grouped by kind |
| `static-refs` | Yes | Non-null static reference fields with retained-size analysis |
| `weak-refs` | Yes | WeakReference handles -- alive vs collected |
| `thread-analysis` | Yes | Thread states, blocking objects, stack traces |
| `thread-pool` | Yes | ThreadPool state and queued work items |
| `deadlock-detection` | Yes | Deadlock cycles in the wait graph |
| `async-stacks` | Yes | Suspended async state machines at await points |
| `exception-analysis` | Yes | Exception objects on heap and active threads |
| `event-analysis` | Yes | Event handler leaks -- publisher types, field names, subscriber counts, retained memory |
| `http-requests` | Yes | In-flight HTTP request objects and outbound connection pools (`ServicePoint`) |
| `connection-pool` | Yes | Database connection objects and leak detection |
| `wcf-channels` | Yes | WCF service/channel objects; distinguishes client proxy channels from server-side hosting infrastructure |
| `timer-leaks` | Yes | Timer objects and their callback targets (callback method names fully resolved and decoded) |
| `module-list` | Yes | Loaded assemblies with path and size |
| `gc-roots` | No | GC roots and referrers for a given type (too slow for `--full`) |
| `type-instances` | No | All instances of a given type (`--type <name>` required) |
| `object-inspect` | No | All field values of an object with optional retained-size BFS (`--address <hex>` required) |
| `load` | No | Pre-build all analysis caches for a dump file or every dump in a directory |
| `close` | No | Delete all analysis cache files for a dump file or directory |

---

### `object-inspect`

Inspects all fields of a single managed object. With `--retained` it computes the exclusive retained size of every reference field using BFS.

```
DumpDetective object-inspect <dump-file> --address <hex> [options]

Options:
  -x, --address <hex>        Object address (hex, e.g. 0x00000276DB084170)  [required]
  -d, --depth <N>            Recursion depth into references (default: 1)
  --max-array <N>            Max array elements to show (default: 10)
  --retained, -r             Compute retained size per reference field
  --retained-cap <N>         Max BFS nodes per field (0 = unlimited, default: 0)
  --no-cache                 Ignore existing .bfs.idx cache; use in-request BFS
  --no-save                  Do not save a new .bfs.idx cache after building
  -h, --help                 Show this help
```

**Examples:**
```bash
# Inspect a single object (no retained sizes)
DumpDetective object-inspect app.dmp -x 0x00000276DB084170

# Inspect with retained-size BFS per field (builds cache on first run; fast on repeat runs)
DumpDetective object-inspect app.dmp -x 0x00000276DB084170 --retained

# Recurse 3 levels deep
DumpDetective object-inspect app.dmp -x 0x00000276DB084170 --retained -d 3

# Cap BFS per field to 1M nodes (fast estimate for very deep graphs)
DumpDetective object-inspect app.dmp -x 0x00000276DB084170 --retained --retained-cap 1000000
```

> **Tip:** Run `load` once before `object-inspect --retained` so all caches are pre-built and the first retained-size run is instant.

---

### `load`

Pre-builds all analysis caches for a dump file so that subsequent `analyze --full` runs complete as fast as possible. Accepts either a **single dump file** or a **directory** — when a directory is given every `.dmp`/`.mdmp` file is processed sequentially, one at a time, so peak memory stays bounded. Already-valid caches are skipped; use `--force` to rebuild all.

```
DumpDetective load <dump-file-or-directory> [options]

Options:
  --force, -f    Rebuild all caches even if valid ones already exist
  -h, --help     Show this help
```

**Caches written to `.ddcache\<dump-name>\` alongside the dump:**

| Cache file | Contents |
|---|---|
| `stringGroups.bin` | String-duplicates data |
| `fragmentation.bin` | Heap fragmentation segments and distribution |
| `gc-roots.bin` | All GC root addresses and types |
| `static-roots.bin` | Statically-rooted object addresses |
| `hot-addr-types.bin` | Referencing-type breakdown for top-30 objects |
| `finalizer-queue.bin` | Finalizer queue per-type stats and thread info |
| `<dump>.bfs.idx` | Brotli-compressed forward-reference BFS graph (CSR format) |
| `<dump>.parent.map` | Child-to-parent address map (~1.2–1.3 GB typical) |

The BFS index is built in 3 passes with 8 parallel workers. Pass 1 enumerates all live objects and records shallow sizes. Pass 2 counts outbound references per node to size the CSR arrays. Pass 3 fills the edge arrays. The result is validated against the dump's file size and last-write timestamp on every load — a stale or mismatched cache is rebuilt automatically.

**Measured BFS build timings**

| Phase | ~87 M nodes | ~95 M nodes | ~111 M nodes |
|---|---:|---:|---:|
| Pass 1 — enumerate | 47.6 s | 48.0 s | 52.6 s |
| Pass 2 — count edges | 66.5 s | 68.0 s | 74.0 s |
| Pass 3 — fill edges | 67.1 s | 68.7 s | 74.0 s |
| Save (Brotli Optimal) | 30.7 s | 32.4 s | 36.6 s |
| **Total build** | **211.9 s** | **217.1 s** | **237.2 s** |
| Load (subsequent runs) | 9.8 s | 10.2 s | 11.5 s |

With caches loaded, sub-reports in `analyze --full` complete much faster than cold runs because BFS-heavy analyzers reuse the pre-built index. On the latest ~25 GB benchmark, sub-report wall time was **28.5s** without plugins (29 reports) and **22.7s** with Example plugins (32 reports). Trace analyzers also cache repeated event-name classification so large `.nettrace` / `.etl` files avoid re-evaluating the same provider/event strings on every event. See [Performance & Resource Expectations](#performance--resource-expectations) for the full side-by-side benchmark.

**Cache tradeoff (~100 M object heap):**

- `.bfs.idx` file size: ~725 MB; `parent.map`: ~1.2–1.3 GB
- Additional RAM after cache load: ~3 GB
- Retained-size work avoided per run: 3–7 min
- Typical full-report wall-clock reduction: ~600–800 s → ~200–400 s

If RAM is tight, use targeted commands or skip cache loading with `--no-cache`.

**Examples:**
```bash
# Pre-build all caches for a single dump (one-time setup)
DumpDetective load app.dmp

# Force rebuild (e.g. after a code update)
DumpDetective load app.dmp --force

# Pre-build caches for all dumps in a directory (skips already-valid caches)
DumpDetective load D:\dumps

# Then use instantly in object-inspect or analyze --full
DumpDetective object-inspect app.dmp -x 0x00000276DB084170 --retained
DumpDetective analyze app.dmp --full
```

---

### `close`

Deletes all analysis cache files created by `load` or by `analyze --full` for a given dump. Accepts a **single dump file** or a **directory**. Use `--dry-run` to preview what would be deleted without removing anything.

```
DumpDetective close <dump-file-or-directory> [options]

Options:
  --dry-run   Show what would be deleted without deleting
  -h, --help  Show this help
```

**Examples:**
```bash
# Delete all caches for a single dump
DumpDetective close app.dmp

# Preview what would be deleted
DumpDetective close app.dmp --dry-run

# Delete caches for all dumps in a directory
DumpDetective close D:\dumps
```

---

### Trace Commands

These commands accept a trace file, not a memory dump. Supported trace inputs are `.nettrace` and `.etl`. `trace-dump-analyze` additionally requires a `.dmp` file.

| Command | Description |
|---|---|
| `trace-analyze` | Combined trace report that opens the trace once and runs all ten trace analyzers in sequence |
| `trace-dump-analyze` | Cross-source analysis: trace + dump together with 10 correlation rules |
| `cpu-trace` | CPU hot path, top methods, and call tree analysis |
| `alloc-trace` | Allocation hotspot analysis based on `GCAllocationTick` events |
| `gc-trace` | GC pause analysis, trigger reasons, and per-collection heap metrics |
| `exceptions-trace` | First-chance exception volume, type breakdown, and flood detection |
| `contention-trace` | Lock contention hotspot and wait-time analysis |
| `threadpool-starvation` | ThreadPool starvation detection from wait and adjustment events |
| `async-trace` | Async Task scheduling, sync-over-async hotspots, continuation call sites |
| `jit-trace` | JIT compilation time, slowest methods, top modules by compilation load |
| `http-trace` | HTTP request latency, top endpoints, error rates |
| `sql-trace` | SQL/EF query latency, slow query list, per-database summary |

### `trace-analyze`

Opens a trace file once and runs the trace analyzers as a single combined report. This is the trace equivalent of a combined dump analysis run.

```
DumpDetective trace-analyze <trace-file> [options]

Supported input formats:
  .nettrace    EventPipe trace collected with a suitable profile
  .etl         Windows ETW trace

Options:
  -n, --top <N>            Top N items per section (default: 20)
  --process <name>         Filter to a specific process name
  --show-system            Include system/kernel frames in CPU tree (default: hidden)
  --with-plugins           Include plugin sub-analyzers (default: excluded)
  -o, --output <file>      Write report to file (.html / .md / .txt / .json / .bin)
  --format <fmt>           Output format shorthand: html|md|json|bin
  -h, --help               Show this help
```

**Included sub-reports:**

- `cpu-trace` for CPU hot paths and call trees.
- `alloc-trace` for allocation hotspots.
- `gc-trace` for GC pause timing and trigger reasons.
- `exceptions-trace` for exception flood detection.
- `contention-trace` for lock hotspots and wait times.
- `threadpool-starvation` for ThreadPool starvation signals.
- `async-trace` for async Task scheduling and sync-over-async blocking.
- `jit-trace` for JIT compilation cost and warm-up overhead.
- `http-trace` for HTTP request latency and error rates.
- `sql-trace` for SQL/EF query latency and slow queries.

Pass `--with-plugins` to append any loaded plugin sub-analyzers to the report (see [Plugin System](Docs/Plugins.md)).

**Examples:**
```bash
DumpDetective trace-analyze app.nettrace
DumpDetective trace-analyze perf.etl --process w3wp --output trace-report.html
DumpDetective trace-analyze app.nettrace --top 30 --show-system
DumpDetective trace-analyze perf.etl --with-plugins --output full-report.html
```

### Individual trace analyzers

Use these when you want a single signal instead of the combined `trace-analyze` report:

```bash
DumpDetective cpu-trace app.nettrace --top 40 --output cpu.html
DumpDetective alloc-trace app.nettrace --process w3wp --output alloc.html
DumpDetective gc-trace perf.etl --process w3wp --top 50 --output gc.html
DumpDetective exceptions-trace app.nettrace --output exceptions.html
DumpDetective contention-trace perf.etl --process w3wp --output contention.html
DumpDetective threadpool-starvation perf.nettrace --top 50 --output starvation.html
DumpDetective async-trace app.nettrace --output async.html
DumpDetective jit-trace app.nettrace --output jit.html
DumpDetective http-trace perf.etl --process w3wp --slow-ms 500 --output http.html
DumpDetective sql-trace perf.etl --process w3wp --slow-ms 500 --output sql.html
```

Common trace use cases:

- Use `cpu-trace` when you need hot methods, hot paths, and a call tree.
- Use `alloc-trace` when the problem is allocation churn or GC pressure.
- Use `gc-trace` when you need pause distributions, trigger reasons, or explicit `GC.Collect()` detection.
- Use `exceptions-trace` when a service is throwing at high volume or hiding error floods.
- Use `contention-trace` when threads are blocked on locks and you need hotspot call sites.
- Use `threadpool-starvation` when the runtime is under worker-thread pressure or sync-over-async blocking is suspected.
- Use `async-trace` when you need to detect `.Wait()` / `.Result` call sites and async scheduling delays.
- Use `jit-trace` when cold-start or warm-up is taking too long.
- Use `http-trace` when HTTP endpoint latency or error rates are elevated.
- Use `sql-trace` when SQL query performance or connection pool pressure is suspected.
- Use `trace-dump-analyze` when you have both a trace and a dump from the same incident — it surfaces the highest-confidence root causes by cross-correlating both sources.

---

## Output Formats

Specify an output file with `-o` / `--output`, or use `--format` without a filename:

| Extension | `--format` value | Format |
|---|---|---|
| `.html` | `html` | Interactive HTML — sticky sidebar nav, grouped/collapsible sub-report navigation, built-in charts, sortable/filterable paged tables, **dark mode toggle**, styled alert cards |
| `.md` | `md` | Markdown — suitable for wiki pages or GitHub |
| `.json` | `json` | Structured JSON — full report data, re-renderable to any other format with `render` |
| `.bin` | `bin` | Brotli-compressed JSON — same structure as `.json`, ~50–70% smaller, non-human-readable |
| `.txt` | `txt` | Plain text |

### Default output

When `-o` / `--output` and `--format` are both omitted, every command writes `<dump-name>.html` alongside the dump file.

### Multi-output and `--format`

Both `-o` / `--output` and `--format` are **repeatable** — you can write multiple formats in one run:

```bash
# Two explicit output files
DumpDetective heap-stats app.dmp -o report.html -o report.bin

# Two formats — files auto-named from dump name
DumpDetective heap-stats app.dmp --format html --format bin   # -> app.html + app.bin

# Mix -o and --format: explicit path plus extra format(s)
DumpDetective analyze app.dmp --full -o report.html --format bin   # -> report.html + report.bin

# trend-analysis: write snapshot data AND the rendered report in one pass
DumpDetective trend-analysis d1.dmp d2.dmp --full --format html --format bin  # -> trend-analysis.html + trend-analysis.bin

# render: convert to two formats at once
DumpDetective render snapshots.json -o report.html -o report.md
```

`--format` without a full filename auto-names the file after the dump (or input file for `render`):

```bash
DumpDetective heap-stats app.dmp --format md      # -> app.md
DumpDetective heap-stats app.dmp --format bin     # -> app.bin
DumpDetective render snapshots.json --format md   # -> snapshots.md
```

### Dark mode (HTML output)

The HTML report includes a **🌙 Dark mode** toggle button in the sidebar. Your preference is saved in `localStorage` and respected on subsequent opens. The initial theme follows your OS `prefers-color-scheme` setting.

### HTML report UX

The HTML renderer is designed for large real-world dumps and full combined reports. Current capabilities include:

- Grouped sub-report navigation in the sidebar for `analyze --full` and trend-style combined outputs.
- Collapsible nav groups with stable active-section highlighting while scrolling through large reports.
- Self-contained charts and summary visuals embedded directly into the generated HTML.
- Sortable, filterable tables with paging.
- Global rows-per-page control plus per-table override for especially large sections.
- A single output file with embedded CSS and JavaScript, so reports remain portable and easy to share.
- Stack frames, callback method names, and call-tree entries are decoded from CLR compiler-generated notation — lambdas, closures, async state machines, and generic CLR notation — into readable names in every section.

### JSON / binary output and re-rendering

Use `--output report.json` or `--output report.bin` (Brotli-compressed) with **any** command to capture a fully structured report. Both formats preserve all report data — chapters, sections, tables, key-value pairs, alerts, findings, and details accordions — including chapter nav levels and polymorphic element types.

There are two structured formats:

| `format` field | Produced by | Contents |
|---|---|---|
| `"report"` | Any single-dump command with `--output *.json` / `*.bin` | Full rendered report document |
| `"trend-raw"` | `trend-analysis --output *.json` / `*.bin` | Raw snapshot metrics + optional captured sub-reports |

Both are handled transparently by `DumpDetective render` — it auto-detects the format and decompresses `.bin` automatically:

```bash
DumpDetective render heap-stats.json
DumpDetective render heap-stats.bin
DumpDetective render analyze-full.json  --output report.md
DumpDetective render snapshots.json     --baseline 2 --output report.html
DumpDetective render snapshots.bin      --baseline 2 --output report.html
```

The `trend-raw` format is especially useful: save once with `--full`, then re-render at any baseline, format, or time without touching the original dump files. Use `.bin` for long-term archival — it is typically **50–70% smaller** than the equivalent `.json`.

---

## Project Structure

```
DumpDetective.slnx

DumpDetective.Core/               Models, interfaces, shared utilities
  Interfaces/
    ICommand.cs                   Name, Description, IncludeInFullAnalyze, Category, Kind, Run, BuildReport
    IRenderSink.cs                Format-agnostic output interface
    IHeapObjectConsumer.cs        Heap-walk consumer interface
    ITracePlugin.cs               Minimal trace sub-analyzer interface for plugins (Core-only, no Commands ref needed)
  Models/
    DumpSnapshot.cs               All collected metrics for one dump (AOT JSON-serialisable)
    Finding.cs                    Scored finding (severity, category, headline, advice)
    ReportDoc.cs                  Replayable report document model (chapters > sections > elements)
    ThresholdConfig.cs            Configurable scoring / trend thresholds
  Runtime/
    DumpContext.cs                ClrMD DataTarget + ClrRuntime wrapper
    HeapSnapshot.cs               TypeStats, InboundCounts, StringGroups, gen counters
  Utilities/
    CliArgs.cs                    Shared argument parser (--help, --output, DD_DUMP, flags)
    CommandBase.cs                Execute lifecycle, TryHelp, RunStatus
    ExecutionContext.cs           Thread-local verbose suppression and parameter overrides
    OperationTrace.cs             Per-thread operation timing for full-analyze progress display
    DumpHelpers.cs                FormatSize, IsSystemType, OpenDump, SegmentKindLabel
    HealthScorer.cs               Score(DumpSnapshot, ScoringThresholds) -> (Findings, score)
    ProgressLogger.cs             Live spinner + [SCAN] completion lines via Spectre.Console
    SinkFactory.cs                Creates single or multi-output IRenderSink from path lists
    ThresholdLoader.cs            Lazy-loads dd-thresholds.json; silent fallback to defaults

DumpDetective.Analysis.Memory/    ClrMD data collection and heap walking
  DumpCollector.cs                CollectFull / CollectLightweight orchestration
  HeapWalker.cs                   Single EnumerateObjects() call feeding all consumers
  HeapObjectCollector.cs          Manages consumer registration and walk execution
  RuntimeSubCollectors.cs         Thread, handle, module, finalizer-queue sub-collectors
  SnapshotPopulator.cs            Writes consumer results back into DumpSnapshot
  TrendRawSerializer.cs           DumpSnapshot JSON storage for trend analysis
  BfsIndexBuilder.cs              3-pass parallel CSR graph builder for .bfs.idx cache
  BfsIndexCache.cs                Load / validate / save the .bfs.idx cache
  Consumers/                      IHeapObjectConsumer implementations (one concern each)
    TypeStatsConsumer.cs
    InboundRefConsumer.cs
    StringGroupConsumer.cs
    GenCounterConsumer.cs
    AsyncMethodConsumer.cs
    ExceptionCountConsumer.cs
    HttpRequestsConsumer.cs
    ThreadNameConsumer.cs
    ThreadPoolConsumer.cs
    LightweightStatsConsumer.cs
    ReferrerConsumer.cs
    FragmentationConsumer.cs
    EventDetailConsumer.cs
    ConditionalWeakTableConsumer.cs
  Analyzers/                      Per-command analysis logic (pure POCO in / POCO out)
    HeapStatsAnalyzer.cs          ... (one file per memory-dump command)
    SharedReferrerCache.cs        Reverse-reference graph shared by MemoryLeak + HighRefs

DumpDetective.Analysis.Trace/     .nettrace / ETL data collection
  Analyzers/
    CpuTraceAnalyzer.cs           ... (one file per trace command)

DumpDetective.Reporting/          Output format implementations
  Sinks/
    HtmlSink.cs                   Self-contained HTML; inline CSS/JS; sticky nav; virtual scroll
    MarkdownSink.cs
    TextSink.cs
    JsonSink.cs
    BinSink.cs                    Brotli-compressed JSON
    CaptureSink.cs
  Reports/                        Per-command report builders (one file per command)
    AnalyzeReport.cs
    TrendAnalysisReport.cs
    HeapStatsReport.cs            ... (one file per command)
  ReportDocReplay.cs              Replays a ReportDoc through any IRenderSink
  ReportDiffer.cs                 Produces diff ReportDoc from two ReportDoc inputs
  ReportDocSlicer.cs              Extracts sub-chapters by command name or dump index
  ObjectInspectRenderer.cs        Field-level retained-size table builder
  ToolMemoryDiagnostic.cs         Peak working-set / managed-heap / private-bytes poller

DumpDetective.Commands/           ICommand implementations
  Memory/                         Memory-dump commands (namespace DumpDetective.Commands.Memory)
    AnalyzeCommand.cs             Orchestrator; runs FullAnalyzeCommands in parallel
    TrendAnalysisCommand.cs       Cross-dump trend report
    HeapStatsCommand.cs
    MemoryLeakCommand.cs          ... (one file per memory command; 28 total)
  Trace/                          Trace commands (namespace DumpDetective.Commands.Trace)
    TraceAnalyzeCommand.cs        Orchestrator; runs all six trace sub-analyzers
    CpuTraceCommand.cs
    AllocTraceCommand.cs          ... (one file per trace command; 7 total)
  RenderCommand.cs                Convert any JSON/BIN report to any output format
  DiffCommand.cs                  Compare two saved report files
  BuildBfsCommand.cs              Pre-build BFS retained-size index cache

DumpDetective.Cli/                Entry point -- the AOT executable
  Program.cs                      Top-level statements; --debug flag; default HTML output injection
  CommandRegistry.cs              Single source of truth for all ICommand instances; LPT-ordered for parallel analyze --full
  TraceCommandRegistry.cs         Single source of truth for all trace ICommand instances
  HelpPrinter.cs                  Dynamic --help output grouped by ICommand.Category, sectioned by ICommand.Kind

DumpDetective.DiagnosticScenarios/  Standalone console app — per-scenario dump generation for tests
  Program.cs                      Entry point: runs a named scenario, captures a heap dump, exits
  Scenarios.cs                    All 24 scenario setup/teardown implementations

DumpDetective.Tests/              xUnit test project (no AOT) — see Docs/Testing.md

dd-thresholds.json                Override default scoring/trend thresholds (place next to exe)
```

### Dependency graph

```
Cli ──────────────────────────────────────► Commands
 │                                              │
 │                                              ▼
 │                               Analysis.Memory ──────┐
 │                               Analysis.Trace  ──────┤
 │                                                     │
 └──────────────────► Reporting ──────────► Core ◄─────┘

DiagnosticScenarios ────────────────────────────► (standalone; no project refs)
Tests ───────────────────────────────────► Core + Analysis + Reporting + Commands + Cli
                                           DiagnosticScenarios (build dependency; exe copied to test output)
```

---

## Health Score

The `analyze` command produces a score from **0-100** for the dump, deducting points for each finding:

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
| Heap fragmentation 20-40% | -5 |
| Blocked threads 5-20 | -5 |
| Finalizer queue 100-500 | -5 |
| Async backlog 100-500 | -5 |
| Thread pool near capacity | -5 |
| Exception threads > 5 | -5 |
| String duplication > 100 MB | -5 |
| Pinned handles > 2000 | -5 |
| Timer objects > 500 | -5 |

Score labels: **Healthy** (>=85) · **Stable** (>=70) · **Degraded** (>=50) · **Critical** (<50)

Thresholds are fully configurable via `dd-thresholds.json` placed alongside the executable.

---

## Performance & Resource Expectations

DumpDetective processes dumps by walking every managed object on the heap. Run time and memory scale with **object count**, not dump file size. Dump file size is only a rough guide — a largely native-memory process can produce a multi-GB dump file with very few managed objects.

### Measured Full-Analyze Benchmark

The numbers below are from two real runs on the same IIS worker dump (about 25 GB, 86,546,865 managed objects), using `analyze --full` with and without Example plugin commands.

Commands:

- Without plugins: `DumpDetective analyze w3wp.exe_260421_175618.dmp --full`
- With plugins: `DumpDetective analyze w3wp.exe_260421_175618.dmp --full --with-plugins`

Included plugin commands in the plugin run: `duplicate-modules`, `namespace-heap`, `thread-hotspots`.

**End-to-end comparison (same dump)**

| Metric | Without plugins | With Example plugins | Delta |
|---|---:|---:|---:|
| Collection total | 156.7s | 168.6s | +11.9s |
| Heap walk | 132.6s | 144.6s | +12.0s |
| Finalizer queue scan | 22.2s | 22.7s | +0.5s |
| BFS index load | 16.3s | 10.5s | -5.8s |
| Sub-reports wall time (parallel) | 28.5s (29 reports) | 22.7s (32 reports) | -5.8s |
| Total execution time | 203.1s | 203.4s | +0.3s |

**Peak memory comparison**

| Metric | Without plugins | With Example plugins | Delta |
|---|---:|---:|---:|
| Working set peak | 11.00 GB | 11.96 GB | +0.96 GB |
| Managed heap peak | 9.07 GB | 8.92 GB | -0.15 GB |
| Private bytes peak | 11.96 GB | 12.86 GB | +0.90 GB |

**Collection throughput comparison**

| Step | Without plugins | With Example plugins |
|---|---:|---:|
| Thread scan | 158 objs, 417ms, ~378/s | 158 objs, 424ms, ~372/s |
| Handle scan | 12,687 objs, 1.1s, ~11,639/s | 12,687 objs, 658ms, ~19,281/s |
| Heap walk | 86,546,865 objs, 132.6s, ~652,676/s | 86,546,865 objs, 144.6s, ~598,530/s |
| Finalizer queue | 4,248,457 objs, 22.2s, ~191,449/s | 4,248,457 objs, 22.7s, ~187,520/s |

**Plugin analyzer overhead in the full run**

| Plugin analyzer | Time |
|---|---:|
| duplicate-modules | 14ms |
| namespace-heap | 37ms |
| thread-hotspots | 5.0s |

The plugin-enabled run produced effectively the same total wall-clock time on this dataset (+0.3s overall), with a modest increase in peak working set.

### Heap walk throughput

The single-pass heap walk (which feeds all analysis consumers simultaneously) typically runs at roughly **500,000–1,500,000 objects/second** on production machines, with the lower end common for very large heaps and plugin-enabled full runs.

See the measured benchmark above for a concrete large-dump example.

### Combined estimates per dump

| Dump file size | Typical object count | `analyze --full` (wall clock) | Peak working set |
|---|---|---|---|
| < 500 MB | < 1 M | < 5 s | < 300 MB |
| 500 MB – 4 GB | 1 – 15 M | 10–30 s | < 2 GB |
| 4 – 15 GB | ~15 – 50 M | 1–3 min | 2–6 GB |
| 15 – 30 GB | ~50 – 120 M | 5–8 min (with cache) / 15–25 min (first run) | 12–17 GB |

> Object count is what actually drives analysis time, not file size. Use `--debug` on a first run to see the exact object count for your dump.
>
> `analyze --full` runs all built-in sub-reports in parallel (29 in the current build). With `--with-plugins`, plugin sub-reports are appended (32 in the Example plugin benchmark). `analyze` without `--full` finishes right after collection — the table above shows `--full` times.

### What drives `--full` time

`analyze --full` runs sub-reports in parallel; wall-clock time equals the **slowest** sub-report, not their sum.

Latest measured longest sub-reports on the ~25 GB / 86.5 M object dataset:

| Run | Top 1 | Top 2 | Top 3 | Top 4 | Top 5 |
|---|---|---|---|---|---|
| Without plugins | `large-objects` 28.5s | `static-refs` 24.6s | `closure-capture` 16.3s | `gc-root-map` 15.8s | `event-analysis` 3.7s |
| With plugins | `large-objects` 22.7s | `static-refs` 21.2s | `closure-capture` 12.6s | `gc-root-map` 12.3s | `thread-hotspots` 5.0s |

### Memory usage

Peak working set is driven by the referrer-graph BFS (`memory-leak` + `high-refs`), which builds a full reverse-reference map in memory while the other sub-reports run in parallel.

**Rule of thumb: plan for roughly 0.5–0.6× the dump file size in free RAM when running `--full`.**

Verified against real dumps:

| Dump size | Object count | Peak working set | Ratio |
|---|---|---|---|
| 3.65 GB | 10.7 M | 2.09 GB | 0.57× |
| ~25 GB (no plugins) | 86.5 M | 11.00 GB | 0.44× |
| ~25 GB (`--with-plugins`) | 86.5 M | 11.96 GB | 0.48× |

The ratio stays well below 1× because:
- ClrMD memory-maps the dump rather than loading it — only touched pages are resident.
- The BFS map stores only 1 parent address per object (16 B/entry) rather than the full object graph.
- Large structures (`InboundCounts`, `StringGroups`, `BfsMap`) are released as soon as their last consumer finishes, not held for the full run.

### `trend-analysis --full` across multiple dumps

Each dump is processed **sequentially** — loaded, analysed, fully released — before the next one begins. Peak memory equals the most expensive single dump in the set, not the sum of all dumps.

#### Measured benchmark: 3 production IIS dumps (~25 GB each)

The numbers below come from a real `trend-analysis . --full` run against three production IIS worker dumps taken within minutes of each other.

**Dump inventory**

| Dump | Object count | BFS nodes | BFS edges |
|---|---:|---:|---:|
| D1 | 86,546,865 | 87,104,236 | 246,874,211 |
| D2 | 110,472,530 | 111,252,975 | 290,156,029 |
| D3 | 94,789,885 | 95,441,188 | 263,506,045 |

**End-to-end wall-clock time**

| Run | BFS caches | Total time | vs. no-cache |
|---|---|---:|---|
| Without pre-built caches | Built on-demand per dump | 1827.9 s (~30.5 min) | baseline |
| With pre-built caches (`load` run first) | Loaded from disk | 365.3 s (~6.1 min) | **~5× faster** |

**Per-dump breakdown — without cache**

| Dump | Heap walk | BFS build | Sub-reports (wall) |
|---|---:|---:|---:|
| D1 (86.5 M objs) | 83.9 s | 211.9 s (3-pass + save) | 234.1 s |
| D2 (110.5 M objs) | 121.2 s | 237.2 s | 298.6 s |
| D3 (94.8 M objs) | 87.4 s | 217.1 s | 296.8 s |

BFS build breakdown (3-pass): pass 1 enumerate ~48–53 s, pass 2 count edges ~67–74 s, pass 3 fill edges ~67–74 s, save (Brotli) ~31–37 s.

**Per-dump breakdown — with pre-built caches**

| Dump | Heap walk | BFS load | Sub-reports (wall) |
|---|---:|---:|---:|
| D1 (86.5 M objs) | 83.0 s | 9.8 s | 6.2 s |
| D2 (110.5 M objs) | 93.0 s | 11.5 s | 6.1 s |
| D3 (94.8 M objs) | 86.8 s | 10.2 s | 7.0 s |

With caches loaded, all 23 sub-reports complete in ~6–7 s per dump. Without caches, the five BFS-heavy sub-reports (`static-refs`, `heap-fragmentation`, `event-analysis`, `memory-leak`, `high-refs`) each take 150–300 s and dominate wall-clock time.

**Peak memory usage**

| Run | Peak working set | Peak managed heap | Peak private bytes |
|---|---:|---:|---:|
| Without cache | 13.56 GB | 13.01 GB | 14.18 GB |
| With cache | 15.04 GB | 12.30 GB | 16.12 GB |

The with-cache run peaks slightly higher on working set because the BFS index (~3.5–4.4 GB per dump) remains mapped in memory while the parallel sub-reports execute. Without cache, the BFS is built then partially freed before sub-reports start. Either way, plan for **14–16 GB free RAM** for a 3-dump run at this scale.

**Pipeline memory deltas — with cache**

| Stage | WS delta | WS after |
|---|---:|---:|
| Load dump | +733 MB | ~760 MB |
| Heap walk + scoring | +5.5–6.4 GB | ~6.3–10.8 GB |
| BFS cache load | +3.5–4.4 GB | ~9.8–14.8 GB |
| Sub-reports (23 parallel) | −361 MB – +421 MB | ~12.9–15.2 GB |
| Render trend report | +3 MB | ~3.6 GB |

Each dump's heap walk and BFS load is the main memory spike. Sub-reports add relatively little because all the expensive BFS traversals read from the already-loaded index rather than building new structures.

**Rule of thumb for `trend-analysis --full`:**

- Heap walk throughput: ~900,000–1,100,000 objects/second per dump.
- Sub-report wall time with caches: dominated by `static-refs` (~6–7 s) — all other sub-reports finish in ≤ 4 s.
- Sub-report wall time without caches: dominated by `heap-fragmentation`, `event-analysis`, `static-refs` (~230–300 s) — run `load` first on any dump you plan to analyze repeatedly.
- Memory peak per dump: roughly **0.5–0.6× dump file size** in free RAM.
- For 3 × ~25 GB dumps: plan for **16+ GB free RAM** and **~6 min** (with caches) or **~30 min** (without).

### Offline `render`

`render` on a pre-saved `.json` completes in **under a second** for any output format. No dump file or ClrMD overhead is involved.

### BFS retained-size cache (`.bfs.idx`)

Run `load` once per dump to pre-build all analysis caches. Cache reuse removes repeated retained-size graph construction and keeps full-report runs consistent at large object counts. The [`load` command section](#load) covers options, measured build timings across three dump sizes, and the RAM tradeoff. The `trend-analysis --full` benchmark above has a measured end-to-end comparison across three ~25 GB production dumps: **1827.9 s without caches → 365.3 s with, ~5× speedup**. Use `close` to delete all caches when a dump is no longer needed.

---

## Thresholds

Place a `dd-thresholds.json` file next to `DumpDetective.Cli.exe` to override any scoring or trend threshold. Missing or invalid files silently fall back to built-in defaults. See the included `dd-thresholds.json` for the full schema.
