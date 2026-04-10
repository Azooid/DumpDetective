# DumpDetective

A command-line tool for analysing .NET memory dumps (.dmp / .mdmp). Built on **ClrMD 3.x** and **.NET 10**, it produces scored health reports, trend reports across multiple dumps, and targeted diagnostics — all exportable to **HTML, Markdown, or plain text**.

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
dotnet publish -r win-x64 -c Release
```

---

## Quick Start

```bash
# Set a default dump path so you don't have to type it every time
$env:DD_DUMP = "C:\dumps\w3wp.dmp"

# Scored quick-look report in the terminal
DumpDetective analyze

# Full report (all 20+ sub-analyses) exported to HTML
DumpDetective analyze app.dmp --full --output report.html

# Trend report across a series of dumps
DumpDetective trend-analysis d1.dmp d2.dmp d3.dmp --output trends.html

# Or point at a folder — picks up all .dmp files sorted by timestamp
DumpDetective trend-analysis C:\dumps\ --output trends.html
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
  --full               Full combined report (scored summary + all 20+ sub-reports)
  -o, --output <file>  Write report to file (.md / .html / .txt)
```

**What it covers:**
- Health score (0–100) with per-finding deductions
- Findings grouped as Critical / Warning / Info with recommendations
- Memory: heap by generation (SOH / LOH / POH), fragmentation
- Threads: blocked, async backlog, thread pool saturation
- Exceptions, finalizer queue, GC handles (pinned / strong / weak)
- Event handler leaks, string duplication, timer objects
- WCF channels, DB connections, top types by size

**Examples:**
```bash
DumpDetective analyze app.dmp
DumpDetective analyze app.dmp --full --output full-report.html
```

---

### `trend-analysis`

Cross-dump trend report comparing two or more snapshots over time.

```
DumpDetective trend-analysis <dump1> <dump2> [<dump3>...] [options]
DumpDetective trend-analysis <directory> [options]
DumpDetective trend-analysis --list <file.txt> [options]

Options:
  --full                   Full collection per dump (event leaks, string duplicates)
  --ignore-event <type>    Exclude publisher types whose name contains <type> (repeatable)
  -o, --output <f>         Write report to file (.md / .html / .txt)
```

**Report sections:**
| # | Section |
|---|---|
| 0 | Dump Timeline |
| 1 | Incident Summary — signal status table (✓/⚠/✗), per-dump findings accordions, executive paragraph |
| 2 | Overall Growth Summary |
| 3 | Thread & Application Pressure |
| 4 | Event Leak Analysis |
| 5 | Finalizer Queue Detail |
| 6 | Highly Referenced Objects |
| 7 | Rooted Objects Analysis |
| 8 | Duplicate String Analysis |

**Examples:**
```bash
DumpDetective trend-analysis d1.dmp d2.dmp d3.dmp --output trends.html
DumpDetective trend-analysis C:\dumps\ --full --output report.html
DumpDetective trend-analysis --list dumps.txt --full --output report.md
DumpDetective trend-analysis d1.dmp d2.dmp --full --ignore-event SNINativeMethodWrapper
```

---

### Targeted Commands

Each command accepts `<dump-file> -o <output>` and `--help`.

| Command | Description |
|---|---|
| `event-analysis` | Detect event handler leaks — publisher types, field names, subscriber counts, retained memory |
| `heap-stats` | Heap object counts and sizes grouped by type |
| `large-objects` | Large objects on LOH / POH / Gen heap |
| `string-duplicates` | Duplicate strings and wasted memory |
| `thread-analysis` | Thread states, blocking objects, stack traces |
| `deadlock-detection` | Deadlock cycles in the wait graph |
| `exception-analysis` | Exception objects on heap and active threads |
| `gc-roots` | GC roots and referrers for a given type |
| `static-refs` | Non-null static reference fields |
| `http-requests` | In-flight HTTP request objects |
| `timer-leaks` | Timer objects and their callback targets |
| `finalizer-queue` | Objects waiting in the finalizer queue |
| `handle-table` | GC handles grouped by kind |
| `pinned-objects` | Pinned GC handles causing heap fragmentation |
| `gen-summary` | Object counts and sizes by GC generation |
| `heap-fragmentation` | Segment free space and fragmentation percentage |
| `async-stacks` | Suspended async state machines at await points |
| `thread-pool` | ThreadPool state and queued work items |
| `object-inspect` | All field values of an object by address |
| `type-instances` | All instances of a given type |
| `weak-refs` | WeakReference handles — alive vs collected |
| `wcf-channels` | WCF service/channel objects and their state |
| `connection-pool` | Database connection objects and leak detection |
| `high-refs` | Highly-referenced "hub" objects — caches, shared state |
| `module-list` | Loaded assemblies with path and size |

---

## Output Formats

Specify an output file with `-o` / `--output`:

| Extension | Format |
|---|---|
| `.html` | Interactive HTML — sticky sidebar nav, collapsible sections, sortable/filterable tables, styled alert cards |
| `.md` | Markdown — suitable for wiki pages or GitHub |
| `.txt` | Plain text |
| *(none)* | Console (Spectre.Console with colour) |

---

## Project Structure

```
DumpDetective/
├── Program.cs                  Command dispatch & help
├── Collectors/
│   └── DumpCollector.cs        Heap walk — collects snapshot data for all metrics
├── Commands/
│   ├── AnalyzeCommand.cs       Scored health report
│   ├── TrendAnalysisCommand.cs Multi-dump trend analysis
│   └── *.cs                    Individual targeted commands
├── Core/
│   ├── CommandBase.cs          Shared argument parsing, execution wrapper
│   └── DumpContext.cs          ClrMD runtime and heap wrapper
├── Models/
│   ├── DumpSnapshot.cs         All collected metrics for one dump
│   └── Finding.cs              Scored finding (severity, category, headline, advice)
├── Helpers/
│   └── DumpHelpers.cs          Size formatting, type classification
└── Output/
    ├── IRenderSink.cs           Format-agnostic output interface
    ├── HtmlSink.cs              HTML renderer
    ├── MarkdownSink.cs          Markdown renderer
    ├── TextSink.cs              Plain text renderer
    └── ConsoleSink.cs           Spectre.Console renderer
```

---

## Health Score

The `analyze` command produces a score from **0–100** for the dump, deducting points for each finding:

| Signal | Deduction |
|---|---|
| Event leak > 1000 subscribers on a single field | −20 |
| Thread pool saturated | −15 |
| Heap > 2 GB | −15 |
| Finalizer queue > 500 objects | −15 |
| Async backlog > 500 continuations | −10 |
| Heap fragmentation ≥ 40% | −10 |
| DB connections > 50 | −10 |
| LOH > 500 MB | −10 |
| WCF faulted channels | −10 |
| Event leaks (moderate) | −10 |
| Blocked threads > 20 | −10 |
| Heap fragmentation 20–40% | −5 |
| Blocked threads 5–20 | −5 |
| Finalizer queue 100–500 | −5 |
| Async backlog 100–500 | −5 |
| Thread pool near capacity | −5 |
| Exception threads > 5 | −5 |
| String duplication > 100 MB | −5 |
| Pinned handles > 2000 | −5 |
| Timer objects > 500 | −5 |

Score labels: **Healthy** (≥85) · **Stable** (≥70) · **Degraded** (≥50) · **Critical** (<50)
