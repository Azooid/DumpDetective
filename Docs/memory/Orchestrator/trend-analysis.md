# trend-analysis

**Category:** Orchestrator  
**Included in `analyze --full`:** No (multi-dump command)

## What it does

Analyzes multiple dump files captured over time and produces a trend report showing how heap size, object counts, GC generation distribution, thread counts, and other metrics change across captures. The primary tool for confirming a leak is actively growing (not just a historical high-water mark) and for quantifying the rate of growth.

---

## How it works

1. **Dump discovery**: Accepts explicit file paths, a directory (all `.dmp`/`.mdmp` files), or a `--list` file (one path per line). Files are sorted by modification time to establish a natural timeline.
2. **Per-dump collection**: For each dump (in parallel where caches allow), runs `DumpCollector.CollectFull` → produces a `DumpSnapshot` POCO. `CollectFull` performs:
   - Full heap walk with all 10 shared consumers (`HeapWalker.Walk`)
   - Thread + handle enumeration
   - GC handle analysis
   - Segment analysis
3. **Sub-reports** (when `--full` is set): Also runs all 28 `IncludeInFullAnalyze` commands on each dump and captures the resulting `ReportDoc` objects. These are stored as `DumpSnapshot.SubReport` for embedding in the trend output and for later replay via `render`.
4. **Trend aggregation**: Assembles `DumpSnapshot[]`. For each metric, computes:
   - Raw value per dump
   - Delta vs baseline dump (`--baseline`, default: first dump)
   - Trend direction: ↑ (growing), ↓ (shrinking), = (stable), computed from consecutive-dump deltas
5. **Trend tables**: Renders one column per dump. Key metric rows: heap size, Gen0/Gen1/Gen2/LOH/POH bytes, object counts per generation, thread count, finalizer queue depth, pinned count, top-10 types by size (each a row), top-5 types by count, event subscriber totals, active connection counts, active async backlogs.
6. **Output**: Saves full `DumpSnapshot[]` + embedded `ReportDoc[]` (if `--full`) as a single `.json` or `.bin` file. The binary format is Brotli-compressed JSON.

---

## Options

| Option | Description |
|---|---|
| `<dump1> <dump2> ...` | Explicit dump file paths |
| `<directory>` | Directory containing `.dmp` / `.mdmp` files (sorted by mtime) |
| `--list <file>` | Text file with one dump path per line |
| `--full` | Run complete analysis per dump and embed sub-reports |
| `--baseline <n>` | 1-based index of baseline dump for delta computation (default: 1) |
| `--ignore-event <type>` | Exclude event publisher types matching this substring (repeatable) |
| `--prefix <p>` | Column label prefix (default: `D` → D1, D2, D3) |
| `--str-top <n>` | Max string duplicate groups shown |
| `--str-min-count <n>` | Min duplicate count for string groups |
| `--bfs-depth <n>` | BFS sample depth for static-refs per dump |
| `--exact` | Full BFS for static-refs (no node cap) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`, `.bin`) |

---

## What to look for

| Signal | What it means |
|---|---|
| **Object count monotonically increasing (↑↑↑)** | Active leak — every capture has more instances. The delta column shows per-interval growth rate. |
| **Heap size growing across all captures** | Overall memory growth, not just a spike. Compare to object count growth to separate allocation volume from retention. |
| **Gen2 growing while Gen0/Gen1 stable** | Objects surviving GC and being promoted to Gen2. Classic long-lived retention pattern. |
| **Thread count growing** | Thread leak or continuously increasing load. Correlate with CPU usage from trace. |
| **Finalizer queue depth growing** | Finalizer thread falling behind. If `FinalizerBlocked = true` in any snapshot, escalate immediately. |
| **Event subscriber count growing** | Event handler leak — publisher is not unsubscribing consumers on their disposal. |
| **Baseline has no signal** | Use `--baseline 2` to compare everything relative to the second dump (e.g. after a service restart) to isolate the post-restart growth. |

---

## Example usage

```
# Trend across 3 explicit dumps
DumpDetective trend-analysis d1.dmp d2.dmp d3.dmp --output trends.html

# All dumps in a directory (sorted by mtime), full sub-reports per dump
DumpDetective trend-analysis C:\dumps\ --full --output trends.bin

# Re-render later with a different baseline
DumpDetective render trends.bin --baseline 3 --output re-baselined.html

# Extract only the memory-leak sub-reports across all dumps
DumpDetective render trends.bin --command memory-leak --output memleak-trend.html

# Diff two trend runs (week-over-week comparison)
DumpDetective diff week1.bin week2.bin -o delta.html
```
