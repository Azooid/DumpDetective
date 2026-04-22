# DumpDetective

A command-line tool for analysing .NET memory dumps (`.dmp` / `.mdmp`). Built on **ClrMD 3.x** and **.NET 10 Native AOT**, it produces scored health reports, trend reports across multiple dumps, and targeted diagnostics — all exportable to **HTML, Markdown, plain text, or JSON**.

Every command supports `--output report.json`, which captures the full structured report. Use `DumpDetective.Cli render report.json --output report.html` (or any other format) to convert it at any time without re-opening the dump.

---

## Requirements

| Requirement | Version |
|---|---|
| .NET SDK | 10.0+ |
| Target dump runtime | .NET Framework 4.x / .NET Core / .NET 5+ |
| OS | Windows (WinDbg-style dumps) |

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

# Scored quick-look report in the terminal
DumpDetective.Cli analyze

# Full report (all 23 sub-analyses) exported to HTML
DumpDetective.Cli analyze app.dmp --full --output report.html

# Full report with peak memory diagnostics printed at the end
DumpDetective.Cli analyze app.dmp --full --output report.html --debug

# Save full report as JSON, convert to HTML later -- no dump file needed
DumpDetective.Cli analyze app.dmp --full --output report.json
DumpDetective.Cli render report.json --output report.html

# Trend report across a series of dumps
DumpDetective.Cli trend-analysis d1.dmp d2.dmp d3.dmp --output trends.html

# Save raw trend data as JSON (includes all per-dump sub-reports when --full)
DumpDetective.Cli trend-analysis d1.dmp d2.dmp d3.dmp --full --output snapshots.json

# Re-render the raw JSON at a different baseline or format -- no dump files needed
DumpDetective.Cli render snapshots.json --baseline 2 --output report.html

# Or point at a folder -- picks up all .dmp files sorted by timestamp
DumpDetective.Cli trend-analysis C:\dumps\ --output trends.html
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
DumpDetective.Cli analyze <dump-file> [options]

Options:
  --full               Full combined report (scored summary + all 23 sub-reports in parallel)
  --debug              Print peak working set / managed heap / private bytes at exit
  -o, --output <file>  Write report to file (.html / .md / .txt / .json)
                       Default: <dump-dir>/analyze_<dump-filename>.html
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
DumpDetective.Cli analyze app.dmp
DumpDetective.Cli analyze app.dmp --full --output full-report.html
DumpDetective.Cli analyze app.dmp --full --output full-report.html --debug
```

---

### `trend-analysis`

Cross-dump trend report comparing two or more snapshots over time.

```
DumpDetective.Cli trend-analysis <dump1> <dump2> [<dump3>...] [options]
DumpDetective.Cli trend-analysis <directory> [options]
DumpDetective.Cli trend-analysis --list <file.txt> [options]

Options:
  --full                   Full collection per dump (event leaks, string duplicates,
                           and per-dump sub-reports embedded in .json output)
  --baseline <n>           1-based index of the dump to use as the trend baseline (default: 1)
  --ignore-event <type>    Exclude publisher types whose name contains <type> (repeatable)
  -o, --output <f>         Write report to file (.html / .md / .txt)
                           .json -- saves raw snapshot data (re-render any time with 'render')
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
DumpDetective.Cli trend-analysis d1.dmp d2.dmp d3.dmp --output trends.html
DumpDetective.Cli trend-analysis d1.dmp d2.dmp d3.dmp --baseline 2 --output report.html
DumpDetective.Cli trend-analysis d1.dmp d2.dmp d3.dmp --full --output snapshots.json
DumpDetective.Cli trend-analysis C:\dumps\ --full --output report.html
DumpDetective.Cli trend-analysis --list dumps.txt --full --output report.md
DumpDetective.Cli trend-analysis d1.dmp d2.dmp --full --ignore-event SNINativeMethodWrapper
```

---

### `render` / `trend-render`

Converts any DumpDetective JSON file to HTML, Markdown, plain text, or console output -- **no dump file required**.

```
DumpDetective.Cli render <file.json> [options]

Accepted input:
  report     JSON produced by any single-dump command with --output *.json
  trend-raw  JSON produced by trend-analysis --output *.json

Options:
  --baseline <n>         Trend baseline (trend-raw only; default: 1 = first dump)
  --ignore-event <type>  Filter event types (trend-raw only; repeatable)
  --mini                 Trend summary only -- suppress per-dump sub-reports even
                         when they are present in the JSON (trend-raw only)
  --from <n>             Extract dump #N's full sub-report as a standalone file.
                         Requires the JSON to have been saved with --full. 1-based.
  --command <name>       Extract only the named command's chapter(s).
                         Combine with --from to target a single dump.
                         Repeatable: --command memory-leak --command heap-stats
                         Valid names: any command that runs in analyze --full
  -o, --output <file>    Output file (.html / .md / .txt / .json)
                         Omit for console output
```

**Examples:**
```bash
# Standard trend report
DumpDetective.Cli render snapshots.json --output report.html

# Trend summary only (no per-dump sub-reports)
DumpDetective.Cli render snapshots.json --mini --output trend-only.html

# Re-render at a different baseline
DumpDetective.Cli render snapshots.json --baseline 2 --output report-d2base.html

# Extract dump #4's full sub-report as a standalone file
DumpDetective.Cli render snapshots.json --from 4 --output d4-full.html

# Extract just the memory-leak chapter from dump #4
DumpDetective.Cli render snapshots.json --from 4 --command memory-leak --output d4-memleak.html

# Extract memory-leak from every dump, stacked in one file
DumpDetective.Cli render snapshots.json --command memory-leak --output all-memleak.html

# Multiple commands from dump #2
DumpDetective.Cli render snapshots.json --from 2 --command memory-leak --command heap-stats --output d2-subset.html

# Convert a single-dump report JSON to HTML
DumpDetective.Cli render heap-stats.json --output heap-stats.html
```

> **Note:** `--from` and `--command` require `trend-raw` JSON saved with `--full`.
> If the JSON was saved without `--full`, sub-reports are not present and extraction will fail with a clear error message.

---

### Targeted Commands

Each command accepts `<dump-file> -o <output>` and `--help`.
When `-o` / `--output` is omitted, the report is written to `<dump-dir>/<command>_<dump-filename>.html` automatically.

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

Specify an output file with `-o` / `--output`:

| Extension | Format |
|---|---|
| `.html` | Interactive HTML -- sticky sidebar nav, collapsible sections, sortable/filterable tables, styled alert cards |
| `.md` | Markdown -- suitable for wiki pages or GitHub |
| `.json` | Structured JSON -- full report data, re-renderable to any other format with `render` |
| `.txt` | Plain text |
| (none) | Console (Spectre.Console with colour) |

### Default output path

When `-o` / `--output` is omitted, the tool automatically writes to:
- **Single-dump commands**: `<dump-directory>/<command>_<dump-filename>.html`
- **Multi-dump / directory commands**: `<directory>/<command>.html`

### JSON output and re-rendering

Use `--output report.json` with **any** command to capture a fully structured JSON report. The JSON preserves all report data -- chapters, sections, tables, key-value pairs, alerts, findings, and details accordions -- including chapter nav levels and polymorphic element types.

There are two JSON formats:

| `format` field | Produced by | Contents |
|---|---|---|
| `"report"` | Any single-dump command with `--output *.json` | Full rendered report document |
| `"trend-raw"` | `trend-analysis --output *.json` | Raw snapshot metrics + optional captured sub-reports |

Both are handled transparently by `DumpDetective.Cli render` -- it auto-detects the format:

```bash
DumpDetective.Cli render heap-stats.json    --output heap-stats.html
DumpDetective.Cli render analyze-full.json  --output report.md
DumpDetective.Cli render snapshots.json     --baseline 2 --output report.html
```

The `trend-raw` format is especially useful: save once with `--full`, then re-render at any baseline, format, or time without touching the original dump files.

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

The single-pass heap walk (which feeds all analysis consumers simultaneously) runs at roughly **400,000–700,000 objects/second** on typical production machines.

### Combined estimates per dump

| Dump file size | Typical object count | `analyze --full` (wall clock) | Peak working set |
|---|---|---|---|
| < 500 MB | < 1 M | < 5 s | < 500 MB |
| 500 MB – 4 GB | 1 – 15 M | 15–60 s | < 2 GB |
| 4 – 15 GB | ~15 – 50 M | 1–5 min | 2–8 GB |
| 15 – 30 GB | ~50 – 120 M | 8–15 min | 10–15 GB |

> Object count is what actually drives analysis time, not file size. Use `--debug` on a first run to see the exact object count for your dump.
>
> `analyze` (quick, no `--full`) completes shortly after the heap walk — add 10–30 s for scoring and report rendering on top of the heap walk time.

### What drives `--full` time

`analyze --full` runs all 23 sub-reports in parallel; wall-clock time equals the **slowest** sub-report, not their sum. The five slow ones each do additional heap traversals:

| Sub-report | ~10 M objects | ~100 M objects |
|---|---|---|
| `static-refs` | ~15 s | 8–10 min |
| `heap-fragmentation` | ~10 s | 5–8 min |
| `event-analysis` | ~10 s | 5–7 min |
| `memory-leak` / `high-refs` (shared BFS) | ~15 s | 6–8 min |
| `finalizer-queue` | < 1 s | 2–4 min |
| All others | < 1 s | < 30 s |

### Memory usage

Peak working set is driven by the referrer-graph BFS (`memory-leak` + `high-refs`) which holds the full reverse-reference map in memory while sub-reports build in parallel. Plan for **at least as much free RAM as the dump file on disk**, and ideally 1.5–2× when running `--full`.

### `trend-analysis --full` across multiple dumps

Each dump is processed **sequentially** — loaded, analysed, fully released — before the next one begins. Peak memory therefore equals the most expensive single dump in the set, not the sum of all dumps.

Example runtimes from real runs:

| Scenario | Dump sizes | Objects per dump | Total time | Peak RAM |
|---|---|---|---|---|
| Small/medium dumps | 700 MB – 3.3 GB | 200 K – 11 M | ~50 s | ~1.3 GB |
| Large dumps | ~25 GB each | 86 M – 110 M | ~38 min | ~13 GB |

For a folder of large dumps (25 GB each, ~100 M objects), budget roughly **10–12 minutes per dump** and **13–15 GB RAM** at peak.

### Offline `render`

`render` on a pre-saved `.json` completes in **under a second** for any output format. No dump file or ClrMD overhead is involved.

---

## Thresholds

Place a `dd-thresholds.json` file next to `DumpDetective.Cli.exe` to override any scoring or trend threshold. Missing or invalid files silently fall back to built-in defaults. See the included `dd-thresholds.json` for the full schema.