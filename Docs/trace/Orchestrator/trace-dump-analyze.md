# trace-dump-analyze

**Category:** Trace — Combined / Cross-source  
**Included in:** Standalone orchestrator (not nested)

## What it does

The most comprehensive single analysis command. Opens a trace file and a dump file simultaneously, runs all 29 trace sub-analyzers and a lightweight dump heap walk, then passes both output sets through the `CorrelationEngine` to surface cross-source findings with the highest confidence.

Cross-source correlations that are only possible with both inputs:
- A retry storm in the trace matches live `HttpRequestException` / `SqlException` objects in the dump
- ThreadPool starvation in the trace correlates with `Awaiting` async state machines in the dump
- LOH growth trend in the trace matches the current LOH occupancy and fragmentation in the dump
- GC pause spikes in the trace correlate with pinned buffers visible in the dump

---

## Architecture

```
TraceFile ──► TraceAnalyzers (×29) ──┐
                                       ├──► CorrelationEngine ──► RankedFindings
DumpFile  ──► LightweightHeapWalk ──┘
```

The dump walk uses `LoadMode.Passive` (no DAC symbol resolution) so it completes in seconds regardless of dump size. It collects: live exception types, async state machine count and states, ThreadPool queue depth, and LOH/segment sizes.

Plugin commands that implement `ITraceDumpCorrelationRule` are called by the `CorrelationEngine` when `--with-plugins` is specified.

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `<dump>` | Path to `.dmp` or `.mdmp` file |
| `--with-plugins` | Enable plugin correlation rules (see [Plugins](../../Plugins.md)) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |
| `-h, --help` | Show this help |

---

## When to use

Use `trace-dump-analyze` when:
- You have both a trace and a dump from the same incident
- You want the highest-confidence root cause findings
- You want a single report covering both sources

Use `trace-analyze` when you only have a trace file.  
Use `analyze --full` when you only have a dump file.
