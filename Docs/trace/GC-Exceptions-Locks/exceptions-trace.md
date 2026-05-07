# exceptions-trace

**Category:** Trace — GC / Exceptions / Locks  
**Included in `analyze --full`:** No (requires a trace file)

## What it does

Parses first-chance exception events from a `.nettrace` or `.etl` trace to detect exception storms. Reports:
- Total exception count and unique type count
- Top N exception types by frequency with representative message and originating call site
- Most recent N individual exception events (type, message, thread, timestamp, throw site)

---

## Analyzer: `ExceptionsTraceAnalyzer`

**Namespace**: `DumpDetective.Analysis.Trace.Analyzers`

### How it works

Opens the trace via `TraceLog.OpenOrConvert(tracePath)`. Exception events are matched when `EventName` ends with `Exception/Start`, `ExceptionThrown`, or `Exception`, or contains `ExceptionCatchStart`.

For each matching event, the type name is read from payload fields `ExceptionType` then `Type`, and the message from `ExceptionMessage` then `Message`. The throw site is resolved by walking the call stack from leaf to root and skipping frames starting with `System.Runtime`, `System.Exception`, or containing `RaiseException`, `clr!`, or `ntdll`.

All events are collected into a list (for `RecentEvents`, sorted by timestamp descending) and accumulated into a `byType` dictionary (for `TopTypes`, sorted by count descending). `FirstMessage` per type is truncated to 120 characters. `UniqueTypeCount` is the number of distinct type name keys in `byType`.

---

## Collecting the trace

```bash
# dotnet-trace — exception profile
dotnet trace collect --profile exceptions -p <pid>

# Minimum provider
dotnet trace collect \
  --providers 'Microsoft-Windows-DotNETRuntime:0x8014:5' -p <pid>

# PerfView
PerfView /ExceptionMsgs /ClrEvents=Exception collect
```

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `--top <n>` | Top N exception types and most-recent events to show (default: 20) |
| `--process <name>` | Substring match on process name |
| `-o, --output <file>` | Output path |

---

## What to look for

| Signal | What it means |
|---|---|
| **Exception flood (> 1,000 total)** | Exceptions used as control flow — significant CPU overhead, as exception creation captures a stack. |
| **> 10,000 exceptions** | Severe exception storm — application is using try/catch for normal branching. Each allocation drives GC. |
| **`TimeoutException` / `TaskCanceledException` at high rate** | Dependency degradation or under-provisioned timeout. Check connection pool size and upstream health. |
| **`NullReferenceException` from application code frames** | Missing null checks in business logic. These should be eliminated, not caught. |
| **`OperationCanceledException` at very high rate** | Cancellation token propagation — usually acceptable (request cancellations), but verify it's not in a tight retry loop. |
| **Single throw call site dominates** | A specific code path is throwing as normal control flow — refactor to avoid the throw. |
| **`ObjectDisposedException`** | Component used after disposal — check `using` scope or async disposal timing. |
| **Exceptions spiking in a narrow time window** | Correlate with GC pause events, connection pool exhaustion, or deployment events. |
