# gc-trace

**Category:** Trace — GC / Exceptions / Locks  
**Included in `analyze --full`:** No (requires a trace file)

## What it does

Parses GC start/stop event pairs from a `.nettrace` or `.etl` file to report:
- Per-GC pause durations, generation, type (blocking vs background), and trigger reason
- Per-generation statistics (count, total/max/avg pause)
- Heap size before and after each GC
- Longest N GC pauses in the trace

---

## Analyzer: `GcTraceAnalyzer`

**Namespace**: `DumpDetective.Analysis.Trace.Analyzers`

### How it works

Opens the trace via `TraceLog.OpenOrConvert(tracePath)`. Three event types are processed: GC start, GC stop, and heap stats.

`GC/Start` / `GCStart` events create a pending entry keyed by `Count` (the GC index), capturing the start timestamp in `TimeStampRelativeMSec`, the generation from the `Depth` payload field, the trigger reason string from `Reason`, and the blocking/background type from `Type`. The heap size at the moment of GC start is taken from the most recently seen `GCHeapStats` total.

`GC/Stop` / `GCStop` events compute the pause duration as the difference between the stop and start timestamps for the matching GC index. The completed event is appended to the timeline with `HeapSizeAfter = 0`, which is back-filled by the next `GCHeapStats` event. This is necessary because `GCHeapStats` fires after `GCStop`, not before.

`GCHeapStats` events sum `GenerationSize0 + GenerationSize1 + GenerationSize2 + GenerationSize3` and back-fill the most recently completed GC's `HeapSizeAfter`.

Reason codes are normalized: numeric or raw CLR strings (`"0"`, `"AllocSmall"`, etc.) become canonical labels — `AllocSmall`, `Induced`, `LowMemory`, `Empty`, `AllocLarge`, `OutOfSpaceSOH`, `OutOfSpaceLOH`, `InducedNotForced`. GC type codes are similarly normalized: `"0"` / `"NonConcurrent"` → `Blocking`, `"1"` / `"Background"` → `Background`, `"2"` / `"ForegroundInduced"` → `ForegroundInduced`.

The output contains the top N longest pauses (`TopPauses`, sorted by `PauseMs` descending), per-generation aggregate statistics (`GenSummary`), and the full chronological timeline.

---

## Collecting the trace

```bash
# dotnet-trace — gc-verbose profile covers GC/Start, GC/Stop, GCHeapStats
dotnet trace collect --profile gc-verbose -p <pid>

# PerfView
PerfView /GCOnly collect

# dotnet-trace minimum
dotnet trace collect \
  --providers 'Microsoft-Windows-DotNETRuntime:0x1:4' -p <pid>
```

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `--top <n>` | Top N longest GC pauses to show (default: 30) |
| `--process <name>` | Substring match on process name |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## What to look for

| Signal | What it means |
|---|---|
| **Blocking Gen 2 pauses > 100 ms** | Application requests pausing for full GC. Investigate heap size and LOH usage with `heap-stats` and `large-objects` in dump analysis. |
| **Many Gen 0/1 collections** | High allocation rate driving frequent minor GCs. Cross-reference with `alloc-trace` for source types. |
| **`Induced` trigger reason** | Explicit `GC.Collect()` call in code — usually an anti-pattern. Find the call site and remove it. |
| **`OutOfSpaceSOH` or `OutOfSpaceLOH`** | GC triggered because the heap couldn't expand fast enough — possible memory pressure or LOH fragmentation. |
| **Heap not shrinking after Gen 2** | Objects surviving every full GC — active memory leak or unnecessary long-lived retention. Use `memory-leak` in dump analysis. |
| **Background GC replaced by blocking** | LOH allocation or pinned objects preventing background GC from completing — check `pinned-objects` and `large-objects`. |
| **MaxPause >> AvgPause** | Occasional very long pauses (outlier GC events) — often coincide with memory pressure or induced GC calls. |
