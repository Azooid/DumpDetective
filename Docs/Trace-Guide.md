# DumpDetective Trace Guide

Complete standalone reference for trace workflows and trace commands. This guide is intended to be sufficient without opening CLI help.

## Supported Inputs

- `.nettrace`
- `.etl`

`.etl.zip` is not supported.

## When To Use Trace Commands

Use trace analysis for time-based behavior:

- CPU hotspots and call trees
- Allocation churn and allocation site concentration
- GC pause outliers and trigger reasons
- Lock contention hotspots
- Exception flood patterns
- ThreadPool starvation patterns

## Fast Start

```bash
# Combined trace analysis (recommended first pass)
DumpDetective trace-analyze app.nettrace

# Focused follow-ups
DumpDetective cpu-trace app.nettrace --output cpu-report.html
DumpDetective gc-trace perf.etl --process w3wp --top 50 --output gc-report.html
DumpDetective threadpool-starvation perf.etl --top 50 --output starvation.html
```

## Trace Command Index

| Command | Purpose |
|---|---|
| `trace-analyze` | Single combined report from all trace analyzers |
| `cpu-trace` | CPU hot paths, top methods, call tree |
| `alloc-trace` | Allocation hotspots from `GCAllocationTick` |
| `gc-trace` | GC pause stats and trigger analysis |
| `contention-trace` | Contention hotspots and wait analysis |
| `exceptions-trace` | Exception volume/flood analysis |
| `threadpool-starvation` | Starvation signals from wait/adjustment events |

## Full Command Reference

### `trace-analyze`

```text
Usage: DumpDetective trace-analyze <trace-file> [options]

Runs all six sub-analyzers in one pass over the trace:
	- cpu-trace
	- alloc-trace
	- gc-trace
	- contention-trace
	- exceptions-trace
	- threadpool-starvation

Options:
	-n, --top <N>            Top N items per section (default: 20)
	--process <name>         Filter to a specific process name
	--show-system            Include system/kernel frames in CPU tree
	-o, --output <file>      Write report to file (.html / .md / .txt / .json)
	-h, --help               Show this help
```

Examples:

```bash
DumpDetective trace-analyze app.nettrace
DumpDetective trace-analyze perf.etl --process w3wp --output trace-report.html
DumpDetective trace-analyze app.nettrace --top 30 --show-system
```

### `cpu-trace`

```text
Usage: DumpDetective cpu-trace <trace-file> [options]

Parses CPU sampling events and reports:
	- Hot path chain of max CPU consumption
	- Top methods by exclusive CPU time
	- Inclusive/exclusive call tree

Options:
	-n, --top <N>            Top N methods/call roots (default: 20)
	--process <name|pid>     Filter to process name or PID
	--show-system            Include system/kernel frames (hidden by default)
	-o, --output <file>      Write report to file (.html / .md / .txt / .json)
	-h, --help               Show this help
```

Examples:

```bash
DumpDetective cpu-trace app.nettrace
DumpDetective cpu-trace perf.etl --top 40 --process w3wp
DumpDetective cpu-trace app.nettrace --output cpu-report.html
```

### `alloc-trace`

```text
Usage: DumpDetective alloc-trace <trace-file> [options]

Parses sampled GCAllocationTick events and reports:
	- Top allocating types by estimated byte volume
	- Top allocating call sites
	- Dominant-type allocation alerts

Options:
	--top <N>            Top N types/call sites (default: 20)
	--process <name>     Filter to a specific process name
	-o, --output <file>  Write report to file (.html / .md / .txt / .json)
	-h, --help           Show this help
```

Examples:

```bash
DumpDetective alloc-trace app.nettrace
DumpDetective alloc-trace perf.etl --process w3wp --top 30
DumpDetective alloc-trace app.nettrace --output alloc.html
```

### `gc-trace`

```text
Usage: DumpDetective gc-trace <trace-file> [options]

Parses GC Start/Stop pairs and reports:
	- Per-generation counts and pause statistics
	- Longest pauses with trigger reasons and heap sizes
	- Alerts for long blocking Gen2 pauses and explicit GC.Collect()

Options:
	--top <N>            Top N longest pauses (default: 30)
	--process <name>     Filter to a specific process name
	-o, --output <file>  Write report to file (.html / .md / .txt / .json)
	-h, --help           Show this help
```

Examples:

```bash
DumpDetective gc-trace app.nettrace
DumpDetective gc-trace perf.etl --process w3wp --top 50
DumpDetective gc-trace app.nettrace --output gc-report.html
```

### `contention-trace`

```text
Usage: DumpDetective contention-trace <trace-file> [options]

Parses ContentionStart/Stop pairs and reports:
	- Top hotspots by accumulated wait time
	- Worst individual lock-acquisition delays
	- Threads affected and aggregate contention metrics

Options:
	--top <N>            Top N hotspots/events (default: 20)
	--process <name>     Filter to a specific process name
	-o, --output <file>  Write report to file (.html / .md / .txt / .json)
	-h, --help           Show this help
```

Examples:

```bash
DumpDetective contention-trace app.nettrace
DumpDetective contention-trace perf.etl --process w3wp
DumpDetective contention-trace app.nettrace --output contention.html
```

### `exceptions-trace`

```text
Usage: DumpDetective exceptions-trace <trace-file> [options]

Parses exception events and reports:
	- Total thrown exceptions and unique exception types
	- Top exception types and originating call sites
	- Flood detection thresholds (>1,000 and >10,000)

Options:
	--top <N>            Top exception types/recent events (default: 20)
	--process <name>     Filter to a specific process name
	-o, --output <file>  Write report to file (.html / .md / .txt / .json)
	-h, --help           Show this help
```

Examples:

```bash
DumpDetective exceptions-trace app.nettrace
DumpDetective exceptions-trace perf.etl --process w3wp --top 40
DumpDetective exceptions-trace app.nettrace --output exceptions.html
```

### `threadpool-starvation`

```text
Usage: DumpDetective threadpool-starvation <trace-file> [options]

Parses wait and ThreadPool adjustment events to surface starvation patterns.

Options:
	-n, --top <N>        Top wait events to display (default: 20)
	-o, --output <f>     Write report to file (.html / .md / .txt / .json)
	-h, --help           Show this help
```

Examples:

```bash
DumpDetective threadpool-starvation perf.nettrace --top 50 --output starvation.html
DumpDetective threadpool-starvation perf.etl --top 50 --output starvation.html
```

## Suggested Trace Triage Path

1. Run `trace-analyze` first.
2. Identify strongest signal area (CPU, alloc, GC, contention, exceptions, starvation).
3. Run focused command with tuned `--top` and optional `--process` filter.
4. Save outputs for comparison and sharing.
