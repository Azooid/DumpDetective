# DumpDetective

A command-line tool for analysing .NET memory dumps (`.dmp` / `.mdmp`). Built on **ClrMD 3.x** and **.NET 10 Native AOT**, it produces scored health reports, trend reports across multiple dumps, and targeted diagnostics — all exportable to **HTML, Markdown, plain text, JSON, or compressed binary**.

Every command writes an HTML report alongside the dump file by default. Use `--output report.json` (or `.bin`) to save a structured report, then `DumpDetective render report.json` (or `render report.bin`) to convert it to any format at any time without re-opening the dump.

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
| RAM | 16 GB free (for a 25 GB dump) | Peak working set ≈ 0.5–0.6× dump size; OS also needs headroom |
| Storage | **NVMe SSD** | Random I/O across entire dump file; faster SSD = faster heap walk |
| CPU | **8 physical cores (16 logical)** | Heap walk uses 8 workers; a second concurrent walk (event-analysis or heap-fragmentation) can spin up another 8 — 16 logical cores prevents contention |

> **Rule of thumb:** free RAM ≥ 0.6 × dump file size. For a 25 GB dump keep at least 16 GB free. If you are tight on RAM, close other applications before running — the OS will use any free memory as file-system cache for the dump, which speeds up the walk significantly.

> **SSD vs HDD:** ClrMD memory-maps the dump and accesses it with highly random I/O during the heap walk, BFS, and fragmentation scan. An NVMe SSD completes a 25 GB / 110 M object dump in ~6 minutes. A spinning disk will typically take 20–40 minutes for the same dump and may cause the OS to thrash swap.

---

## Installation

Install as a global .NET tool from [NuGet.org](https://www.nuget.org/packages/DumpDetective.Cli):

```bash
dotnet tool install --global DumpDetective.Cli --version 2.2.0
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
dotnet build
```

For a self-contained, AOT-compiled single executable:

```bash
dotnet publish DumpDetective.Cli -r win-x64 -c Release
```

The output is a single native binary: `DumpDetective.Cli.exe`.

---

## Quick Start

```bash
# Set a default dump path so you don't have to type it every time
$env:DD_DUMP = "C:\dumps\w3wp.dmp"

# Default: writes report as app.html alongside the dump file
DumpDetective analyze app.dmp

# Full report (all sub-analyses) exported to HTML
DumpDetective analyze app.dmp --full

# Full report with peak memory diagnostics printed at the end
DumpDetective analyze app.dmp --full --debug

# Choose format without specifying a filename
DumpDetective heap-stats app.dmp --format md      # -> app.md
DumpDetective heap-stats app.dmp --format bin     # -> app.bin (Brotli-compressed)
DumpDetective heap-stats app.dmp --format console # -> terminal output

# Save full report as JSON, convert to HTML later -- no dump file needed
DumpDetective analyze app.dmp --full --output report.json
DumpDetective render report.json

# Save as compressed binary (Brotli), convert later
DumpDetective analyze app.dmp --full --output report.bin
DumpDetective render report.bin

# Write both HTML and bin in one pass
DumpDetective analyze app.dmp --full -o report.html -o report.bin
DumpDetective analyze app.dmp --full --format html --format bin

# Trend report across a series of dumps
DumpDetective trend-analysis d1.dmp d2.dmp d3.dmp

# Save raw trend data as JSON (includes all per-dump sub-reports when --full)
DumpDetective trend-analysis d1.dmp d2.dmp d3.dmp --full --output snapshots.json

# Re-render the raw JSON at a different baseline or format -- no dump files needed
DumpDetective render snapshots.json --baseline 2 --output report.html

# Or point at a folder -- picks up all .dmp files sorted by timestamp
DumpDetective trend-analysis C:\dumps\

# Compare two saved trend files (no dump files needed)
DumpDetective trend-analysis d1.dmp d2.dmp d3.dmp --full --output week1.bin
DumpDetective trend-analysis d4.dmp d5.dmp d6.dmp --full --output week2.bin
DumpDetective diff week1.bin week2.bin -o delta.html
```

---

## Environment Variables

| Variable | Description |
|---|---|
| `DD_DUMP` | Default dump file path. Used automatically when no `.dmp` argument is provided. |

---

## Commands

### `analyze`

Scored health report for a single dump.

```
DumpDetective analyze <dump-file> [options]

Options:
  --full               Full combined report (scored summary + all sub-reports in parallel)
  --debug              Print peak working set / managed heap / private bytes at exit
  -o, --output <file>  Write report to file (.html / .md / .txt / .json / .bin)
                       Repeatable: -o report.html -o report.bin  writes both files
  --format <fmt>       Output format shorthand: html|md|json|bin|console
                       Repeatable: --format html --format bin  writes both files
                       Combined: -o report.html --format bin  auto-adds report.bin
  --output console     Print to terminal instead of writing a file
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
DumpDetective analyze app.dmp --full --output full-report.html --debug
DumpDetective analyze app.dmp --format bin     # Brotli-compressed output
DumpDetective analyze app.dmp --output console # terminal only
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
  --format <fmt>           Format shorthand: html|md|json|bin|console
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

### `render` / `trend-render`

Converts any DumpDetective JSON or compressed binary file to HTML, Markdown, plain text, or console output -- **no dump file required**.

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
                         Use '--output console' to print to terminal
                         Repeatable: -o report.html -o report.bin  writes both files
  --format <fmt>         Format shorthand: html|md|json|bin|console
                         Repeatable: --format html --format bin  writes both files
                         Combined: -o report.html --format bin  auto-adds report.bin
  Default: writes <input-name>.html
```

**Examples:**
```bash
# Default: renders to report.html
DumpDetective render snapshots.json
DumpDetective render report.bin

# Explicit output format
DumpDetective render snapshots.json --output report.html
DumpDetective render snapshots.json --format md

# Print to terminal
DumpDetective render snapshots.json --output console

# Trend summary only (no per-dump sub-reports)
DumpDetective render snapshots.json --mini --output trend-only.html

# Re-render at a different baseline
DumpDetective render snapshots.json --baseline 2 --output report-d2base.html

# Extract dump #4's full sub-report as a standalone file
DumpDetective render snapshots.json --from 4 --output d4-full.html

# Extract just the memory-leak chapter from dump #4
DumpDetective render snapshots.json --from 4 --command memory-leak --output d4-memleak.html

# Extract memory-leak from every dump, stacked in one file
DumpDetective render snapshots.json --command memory-leak --output all-memleak.html

# Multiple commands from dump #2
DumpDetective render snapshots.json --from 2 --command memory-leak --command heap-stats --output d2-subset.html

# Convert a single-dump report JSON / bin to HTML
DumpDetective render heap-stats.json
DumpDetective render heap-stats.bin
```

> **Note:** `--from` and `--command` require `trend-raw` JSON saved with `--full`.
> If the JSON was saved without `--full`, sub-reports are not present and extraction will fail with a clear error message.

---

### Targeted Commands

Each command accepts `<dump-file>` and `--help`.
By default every command writes `<dump-name>.html` alongside the dump file. Use `--output <file>`, `--format <fmt>`, or `--output console` to change this.
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
| `static-refs` | Yes | Non-null static reference fields with retained-size estimates |
| `weak-refs` | Yes | WeakReference handles -- alive vs collected |
| `thread-analysis` | Yes | Thread states, blocking objects, stack traces |
| `thread-pool` | Yes | ThreadPool state and queued work items |
| `deadlock-detection` | Yes | Deadlock cycles in the wait graph |
| `async-stacks` | Yes | Suspended async state machines at await points |
| `exception-analysis` | Yes | Exception objects on heap and active threads |
| `event-analysis` | Yes | Event handler leaks -- publisher types, field names, subscriber counts, retained memory |
| `http-requests` | Yes | In-flight HTTP request objects |
| `connection-pool` | Yes | Database connection objects and leak detection |
| `wcf-channels` | Yes | WCF service/channel objects and their state |
| `timer-leaks` | Yes | Timer objects and their callback targets |
| `module-list` | Yes | Loaded assemblies with path and size |
| `gc-roots` | No | GC roots and referrers for a given type (too slow for `--full`) |
| `thread-pool-starvation` | No | ThreadPool starvation heuristic analysis |
| `type-instances` | No | All instances of a given type (`--type <name>` required) |
| `object-inspect` | No | All field values of an object (`--address <hex>` required) |

---

## Output Formats

Specify an output file with `-o` / `--output`, or use `--format` without a filename:

| Extension / keyword | `--format` value | Format |
|---|---|---|
| `.html` | `html` | Interactive HTML — sticky sidebar nav, collapsible sections, sortable/filterable tables, **dark mode toggle**, styled alert cards |
| `.md` | `md` | Markdown — suitable for wiki pages or GitHub |
| `.json` | `json` | Structured JSON — full report data, re-renderable to any other format with `render` |
| `.bin` | `bin` | Brotli-compressed JSON — same structure as `.json`, ~50–70% smaller, non-human-readable |
| `.txt` | `txt` | Plain text |
| `console` | `console` | Terminal output (Spectre.Console with colour) |

### Default output

When `-o` / `--output` and `--format` are both omitted, every command writes `<dump-name>.html` alongside the dump file. Use `--output console` to print to the terminal instead.

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
    ICommand.cs                   Name, Description, IncludeInFullAnalyze, Run, BuildReport
    IRenderSink.cs                Format-agnostic output interface
    IHeapObjectConsumer.cs        Heap-walk consumer interface
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
    CommandBase.cs                Execute lifecycle, TryHelp, RunStatus, SuppressVerbose
    DumpHelpers.cs                FormatSize, IsSystemType, OpenDump, SegmentKindLabel
    HealthScorer.cs               Score(DumpSnapshot, ScoringThresholds) -> (Findings, score)
    ProgressLogger.cs             Live spinner + [SCAN] completion lines via Spectre.Console
    ThresholdLoader.cs            Lazy-loads dd-thresholds.json; silent fallback to defaults

DumpDetective.Analysis/           ClrMD data collection and heap walking
  DumpCollector.cs                CollectFull / CollectLightweight orchestration
  HeapWalker.cs                   Single EnumerateObjects() call feeding all consumers
  HeapObjectCollector.cs          Manages consumer registration and walk execution
  SharedReferrerCache.cs          Reverse-reference graph; shared between MemoryLeak + HighRefs
  RuntimeSubCollectors.cs         Thread, handle, module, finalizer-queue sub-collectors
  SnapshotPopulator.cs            Writes consumer results back into DumpSnapshot
  TrendRawSerializer.cs           DumpSnapshot JSON storage for trend analysis
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
    ConditionalWeakTableConsumer.cs
  Analyzers/                      Per-command analysis logic (pure POCO in / POCO out)
    HeapStatsAnalyzer.cs
    MemoryLeakAnalyzer.cs
    HighRefsAnalyzer.cs
    HeapFragmentationAnalyzer.cs
    StaticRefsAnalyzer.cs
    EventAnalysisAnalyzer.cs
    ... (one file per command)

DumpDetective.Reporting/          Output format implementations
  Sinks/
    HtmlSink.cs                   Self-contained HTML; inline CSS/JS; sticky nav; virtual scroll
    MarkdownSink.cs
    TextSink.cs
    ConsoleSink.cs
    JsonSink.cs
    CaptureSink.cs
  ReportDocReplay.cs              Replays a ReportDoc through any IRenderSink
  ToolMemoryDiagnostic.cs         Peak working-set / managed-heap / private-bytes poller

DumpDetective.Commands/           ICommand implementations (one file per command; 31 total)
  AnalyzeCommand.cs               Orchestrator; runs FullAnalyzeCommands in parallel
  HeapStatsCommand.cs
  MemoryLeakCommand.cs
  ... (one file per command)

DumpDetective.Cli/                Entry point -- the AOT executable
  Program.cs                      Top-level statements; --debug flag; default HTML output injection
  Configuration/
    CommandRegistry.cs            Single source of truth for all ICommand instances
  Helpers/
    HelpPrinter.cs                Formats --help output

DumpDetective.Tests/              xUnit test project (no AOT)

dd-thresholds.json                Override default scoring/trend thresholds (place next to exe)
```

### Dependency graph

```
Cli ─────────────────────► Commands
 |                              |
 |                              v
 |                          Analysis ─────────┐
 |                                            |
 └──────────────► Reporting ────► Core ◄──────┘
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

### Heap walk throughput

The single-pass heap walk (which feeds all analysis consumers simultaneously) runs at roughly **1,000,000–2,000,000 objects/second** on typical production machines (faster on smaller dumps due to better CPU cache utilisation).

### Combined estimates per dump

| Dump file size | Typical object count | `analyze --full` (wall clock) | Peak working set |
|---|---|---|---|
| < 500 MB | < 1 M | < 5 s | < 300 MB |
| 500 MB – 4 GB | 1 – 15 M | 10–30 s | < 2 GB |
| 4 – 15 GB | ~15 – 50 M | 1–3 min | 2–6 GB |
| 15 – 30 GB | ~50 – 120 M | 5–8 min | 8–14 GB |

> Object count is what actually drives analysis time, not file size. Use `--debug` on a first run to see the exact object count for your dump.
>
> `analyze --full` includes all 23 sub-reports. `analyze` without `--full` finishes right after collection — the table above shows `--full` times.

### What drives `--full` time

`analyze --full` runs all 23 sub-reports in parallel; wall-clock time equals the **slowest** sub-report, not their sum. The five slow ones each do additional heap traversals:

| Sub-report | ~10 M objects | ~100 M objects |
|---|---|---|
| `static-refs` | ~6 s | 3–4 min |
| `heap-fragmentation` | ~5 s | 4–5 min |
| `event-analysis` | ~6 s | 4–5 min |
| `memory-leak` / `high-refs` (shared BFS) | ~6 s | 4–5 min |
| `finalizer-queue` | ~0.5 s | 1–3 min |
| All others | < 0.3 s | < 30 s |

### Memory usage

Peak working set is driven by the referrer-graph BFS (`memory-leak` + `high-refs`), which builds a full reverse-reference map in memory while the other sub-reports run in parallel.

**Rule of thumb: plan for roughly 0.5–0.6× the dump file size in free RAM when running `--full`.**

Verified against real dumps:

| Dump size | Object count | Peak working set | Ratio |
|---|---|---|---|
| 3.65 GB | 10.7 M | 2.09 GB | 0.57× |
| ~25 GB | 110 M | 12.58 GB | 0.50× |

The ratio stays well below 1× because:
- ClrMD memory-maps the dump rather than loading it — only touched pages are resident.
- The BFS map stores only 1 parent address per object (16 B/entry) rather than the full object graph.
- Large structures (`InboundCounts`, `StringGroups`, `BfsMap`) are released as soon as their last consumer finishes, not held for the full run.

| Dump file size | Typical object count | `analyze --full` (wall clock) | Peak working set |
|---|---|---|---|
| < 500 MB | < 1 M | < 5 s | < 300 MB |
| 500 MB – 4 GB | 1 – 15 M | 10–30 s | < 2 GB |
| 4 – 15 GB | ~15 – 50 M | 1–3 min | 2–6 GB |
| 15 – 30 GB | ~50 – 120 M | 5–8 min | 8–14 GB |

### `trend-analysis --full` across multiple dumps

Each dump is processed **sequentially** — loaded, analysed, fully released — before the next one begins. Peak memory therefore equals the most expensive single dump in the set, not the sum of all dumps.

Example runtimes from real runs:

| Scenario | Dump size | Object count | Total time | Peak RAM |
|---|---|---|---|---|
| Load-test w3wp | 3.65 GB | 10.7 M | 12.5 s | 2.09 GB |
| Production w3wp | ~25 GB | 110 M | 381 s (~6.4 min) | 12.58 GB |

For large production dumps (~25 GB, ~100 M objects), budget roughly **6–7 minutes** and **13–16 GB RAM** at peak.

### Offline `render`

`render` on a pre-saved `.json` completes in **under a second** for any output format. No dump file or ClrMD overhead is involved.

---

## Thresholds

Place a `dd-thresholds.json` file next to `DumpDetective.Cli.exe` to override any scoring or trend threshold. Missing or invalid files silently fall back to built-in defaults. See the included `dd-thresholds.json` for the full schema.
