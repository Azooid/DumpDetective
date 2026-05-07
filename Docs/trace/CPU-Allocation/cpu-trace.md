# cpu-trace

**Category:** Trace — CPU / Allocation  
**Included in `analyze --full`:** No (requires a trace file)

## What it does

Parses CPU sampling events from a `.nettrace` or `.etl` trace file to identify CPU hotspots. Produces:
- **Hot path**: the deepest chain of maximum CPU consumption (Visual Studio CPU Usage style)
- **Top methods by exclusive CPU time**: the methods that were actually executing when samples were taken
- **Call tree**: inclusive/exclusive time breakdown per caller → callee chain

---

## Analyzer: `CpuTraceAnalyzer`

**Namespace**: `DumpDetective.Analysis.Trace.Analyzers`

### How it works

Opens the trace via `TraceLog.OpenOrConvert(tracePath)`. CPU samples are matched when `EventName` contains `SampledProfile`, `PerfInfo/Sample`, `Kernel/PerfInfo`, or `cpu-sampling`. An optional process name or PID filter is applied.

For each matching event, `ev.CallStack()` provides a `TraceCallStack` linked list from leaf to root. The frame list is reversed to root-first order and inserted into a mutable trie. Every frame on the path receives `InclusiveSamples++`; only the leaf receives `ExclusiveSamples++`. Frame text is `CodeAddress.FullMethodName`, falling back to `CodeAddress.ModuleName` for unresolved native frames.

When `filterSystem=true` (the default), frames whose `ModuleName` matches `ntoskrnl`, `ntdll`, `clr`, `coreclr`, `webengine4`, `iiscore`, or `aspnet*` are excluded. The `EffectiveRoots` descent skips system-only top-level nodes until reaching the first user/managed frame, preventing `ntoskrnl!KiSystemCall` from dominating the call tree root.

The **hot path** is constructed by starting at the highest-inclusive root and following the highest-inclusive child at each level, stopping when no child has at least 0.5% inclusive samples. A cycle check (`child.InclusiveSamples <= curSamples`) prevents the path from looping back through high-inclusive IIS pipeline frames in merged multi-request traces.

---

## Collecting the trace

```bash
# EventPipe (dotnet-trace) — covers managed code; recommended for .NET 6+
dotnet trace collect --profile cpu-sampling -p <pid>

# ETW via PerfView — covers kernel + managed; recommended for IIS/w3wp
PerfView /KernelEvents=default /ClrEvents=default collect
```

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `-n, --top <n>` | Top N methods and call tree roots (default: 20) |
| `--process <name\|pid>` | Substring match on process name or exact PID |
| `--show-system` | Include kernel/system frames in call tree and hot path |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## What to look for

| Signal | What it means |
|---|---|
| **Hot path terminates in a single business-logic method** | CPU bound on one operation — optimize the algorithm or parallelize. |
| **`System.Text.Json` or `Newtonsoft.Json` dominant** | Heavy serialization CPU cost — consider cached/compiled serializers or reducing serialization calls. |
| **GC frames at top of exclusive list** | Allocation pressure driving GC CPU. Cross-reference with `alloc-trace` for the source types. |
| **`Monitor.Enter` / `JIT_MonEnter` exclusive samples** | Lock spinning — cross-reference with `contention-trace` for the hotspot call sites. |
| **IIS/ASP pipeline dominant** | Normal for IIS-hosted apps; check the top user-code frame below the pipeline entry for the real hotspot. |
| **Very short hot path (depth < 3)** | Either CPU is well distributed across many paths (good) or sampling interval is too coarse to resolve deep stacks — use `PerfView` with finer kernel events. |
