# alloc-trace

**Category:** Trace — CPU / Allocation  
**Included in `analyze --full`:** No (requires a trace file)

## What it does

Parses `GCAllocationTick` events (sampled ~every 100 KB allocated) to report the top allocating types by estimated byte volume and the top allocating call sites. Identifies which code paths are generating the most garbage and driving GC pressure.

---

## Analyzer: `AllocTraceAnalyzer`

**Namespace**: `DumpDetective.Analysis.Trace.Analyzers`

### How it works

Opens the trace via `TraceLog.OpenOrConvert(tracePath)` and iterates all events. Events are matched when `EventName` ends with `GCAllocationTick`, equals `GC/AllocationTick`, or contains `AllocationTick`. An optional process name substring filter is applied.

For each matching event, the type name is read from payload fields in priority order: `TypeName`, then `AllocationTypeName`, then `(unknown)`. The byte count is read from `AllocationAmount64` first, then `AllocationAmount`; if neither is present, 100,000 bytes (100 KB, the approximate default tick size) is assumed.

The call stack is walked from leaf to root to find the top user-code frame (`TopUserFrame`): the first frame whose `FullMethodName` does not start with `System.GC`, `System.Runtime.CompilerServices`, or contain `clr!`, `ntdll`, or `coreclr`. This is the call site displayed in the output.

Two accumulation dictionaries are maintained: `byType[typeName]` for per-type tick and byte totals, and `byCallSite["{frame}|{typeName}"]` for per-call-site totals. After iteration, both are sorted by bytes descending and the top N are returned.

**Sampling note**: `GCAllocationTick` fires approximately every 100 KB of allocation per thread. If `AllocationAmount64` is present, the actual byte count is used; otherwise 100 KB is assumed per tick. Totals are estimates, and types with many small allocations may be under-represented relative to types with fewer large allocations.

---

## Collecting the trace

```bash
# dotnet-trace — gc-verbose profile includes GCAllocationTick
dotnet trace collect --profile gc-verbose -p <pid>

# Minimum provider for just allocation ticks
dotnet trace collect \
  --providers 'Microsoft-Windows-DotNETRuntime:0x1:5' -p <pid>

# PerfView
PerfView /GCOnly /AllocSampling collect
```

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `--top <n>` | Top N types and call sites to show (default: 20) |
| `--process <name>` | Substring match on process name |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## What to look for

| Signal | What it means |
|---|---|
| **`System.String` dominating** | String creation at high rate — check for string concatenation in loops, format calls, or response-body building. Use `StringBuilder` or `string.Create`. |
| **`System.Byte[]` dominating** | Buffer allocation per request — use `ArrayPool<byte>` or `MemoryPool<T>`. |
| **Single call site producing most allocations** | Concentrate optimization effort there — `ObjectPool<T>`, `ArrayPool`, or caching the result. |
| **LOH types (`> 85 KB`) in alloc trace** | Each large object allocation forces extra GC bookkeeping. Identify the type and either pool it or reduce its size below the LOH threshold. |
| **Application domain model types at high volume** | Evaluate if instances are being pooled or cached. Normal under load, but verify they're not being accidentally retained. |
| **High tick count but low total bytes** | Many small allocations — consider struct instead of class, or avoid allocating at all in hot paths (use stack-allocated `Span<T>`). |
