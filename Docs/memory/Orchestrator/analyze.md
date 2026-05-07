# analyze

**Category:** Orchestrator  
**Included in `analyze --full`:** No (top-level orchestrator)

## What it does

Produces a scored health report for a single dump file. In default (mini) mode, runs a fast lightweight collection and scores the dump across 9 health dimensions. In `--full` mode, runs all 28 `IncludeInFullAnalyze` commands in parallel and embeds their reports as chapters in a single combined document.

---

## Architecture

### Mini mode (default — fast scored summary)

1. Opens the dump via `DumpContext.OpenDump(path)` — creates `DataTarget` + `ClrRuntime`
2. Calls `DumpCollector.CollectLightweight` — a single parallel `HeapWalker` pass with all 10 consumers:
   - `TypeStatsConsumer`, `InboundRefConsumer`, `StringGroupConsumer`, `GenCounterConsumer`
   - `ExceptionCountConsumer`, `AsyncMethodConsumer`, `TimerConsumer`, `WcfStateConsumer`
   - `ConnectionConsumer`, `EventLeakConsumer`
3. Calls `DumpCollector.CollectThreads` — reads runtime thread structures and stack frames
4. Calls `DumpCollector.CollectHandles` — reads GC handle table counts
5. Passes resulting `DumpSnapshot` to `HealthScorer.Score(snap, thresholds)` → produces `(IReadOnlyList<Finding>, int score)` (0–100, lower = worse)
6. Renders a `ReportDoc` with scored findings, top types, thread summary, exception summary, leak signals

### `--full` mode — comprehensive combined report

1. Performs full lightweight collection first (same as mini)
2. Iterates `CommandRegistry.FullAnalyzeCommands` (28 commands):
   - **Thread-safe commands** (`IAnalyzer.IsThreadSafe == true`): run in parallel via `Parallel.ForEach`
   - **Non-thread-safe commands**: run sequentially
3. Pre-warm caches are used automatically:
   - Commands call `ctx.GetAnalysis<T>()` first — if the data is in session cache (from the shared consumer walk), they skip the heap walk entirely
   - Disk caches (`.ddcache/<name>/`) are also loaded if available from a previous `load` run
4. Each command returns a `ReportDoc` — collected in order, then replayed into the output sink
5. Output sink produces the combined document in the target format

**Commands included in `--full`** (all with `IncludeInFullAnalyze = true`):  
`heap-stats`, `gen-summary`, `high-refs`, `string-duplicates`, `memory-leak`, `heap-fragmentation`, `large-objects`, `pinned-objects`, `finalizer-queue`, `handle-table`, `static-refs`, `weak-refs`, `thread-analysis`, `thread-pool`, `deadlock-detection`, `async-stacks`, `exception-analysis`, `event-analysis`, `http-requests`, `connection-pool`, `wcf-channels`, `timer-leaks`, `module-list` + trend-related sections if trend data is present

---

## Health scoring

`HealthScorer.Score(DumpSnapshot snap, ScoringThresholds thresholds)`:
- Takes pure POCO inputs — no ClrMD types
- Evaluates each field against configured thresholds from `dd-thresholds.json`
- Each threshold breach adds a `Finding` record: `(string Title, string Detail, FindingSeverity Severity)` where `Severity` ∈ `{Critical, Warning, Info}`
- Score formula: `100 − (sum of Severity weights for all breaches)`, clamped to 0

**Default critical thresholds** (from `dd-thresholds.json` defaults):
- Gen2 > 5 GB
- LOH > 1 GB  
- Finalizer thread blocked
- Deadlock cycle detected
- Thread count > 1000
- Event subscribers total > 50,000
- Active connections > 500
- Async backlog > 10,000

---

## Options

| Option | Description |
|---|---|
| `<dump>` | Path to `.dmp` or `.mdmp` file (or set `DD_DUMP` env var) |
| `--full` | Full combined report (scored summary + all sub-reports) |
| `--persist` | Keep all `.ddcache` temp files after analysis for reuse |
| `--str-top <n>` | Max string duplicate groups shown (default: 100) |
| `--str-min-count <n>` | Min duplicate count for string groups (default: 2) |
| `--str-min-waste <bytes>` | Min wasted bytes for string groups (default: 0) |
| `--bfs-depth <n>` | BFS sample depth for static-refs (default: auto) |
| `--exact` | Force exact BFS for static-refs (no node cap) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`, `.bin`) |

---

## Typical workflow

```
# Pre-build caches once (front-loads cost; subsequent runs are fast)
DumpDetective load app.dmp

# Full analysis with all sub-reports
DumpDetective analyze app.dmp --full --output report.html

# Quick triage without pre-built caches (mini mode)
DumpDetective analyze app.dmp

# Save full report for trend analysis later
DumpDetective analyze app.dmp --full --output d1.bin
```

---

## What to look for in the output

| Signal | What it means |
|---|---|
| **Health score < 70** | Multiple threshold breaches — follow up with the Critical and Warning finding details |
| **Critical: finalizer thread blocked** | Finalization stopped — unmanaged resources (handles, connections) not being released. Run `finalizer-queue` + `deadlock-detection`. |
| **Critical: LOH > 1 GB** | Large object fragmentation risk. Run `large-objects` and `heap-fragmentation`. |
| **Critical: deadlock cycle** | Threads stuck permanently. Run `deadlock-detection` for the exact cycle. |
| **Warning: thread count > 500** | Thread pool saturation or thread-per-request pattern. Run `thread-pool` + `async-stacks`. |
| **Warning: event subscribers > 10,000** | Event handler leak. Run `event-analysis`. |
| **Warning: async backlog > 1,000** | Many async operations suspended. Run `async-stacks` for method breakdown. |
