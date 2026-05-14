# jit-trace

**Category:** Trace — JIT / HTTP  
**Included in `trace-analyze`:** Yes

## What it does

Parses JIT compilation events from a `.nettrace` or `.etl` trace file to measure the cost of method compilation at runtime. Reports:

- Total methods JIT-compiled and unique method count
- Total and average JIT time (when timing events are present)
- **Slowest methods to JIT** — individual compilations ordered by wall-clock time
- **Most-compiled methods** — methods compiled more than once (dynamic emit, `Expression.Compile`, re-JIT)
- **Top modules by JIT load** — which assemblies drove the most compilation activity
- A donut chart of methods compiled per module

---

## Events consumed

| Event | Purpose |
|---|---|
| `Method/JittingStarted` | Marks start of JIT compilation; provides method name, namespace, IL size |
| `Method/LoadVerbose` | Marks end of JIT compilation; paired with `JittingStarted` to compute duration |
| `Method/Load` (non-verbose) | Count-only fallback when verbose events are absent |

Start and stop events are matched by `MethodID` field. The call stack capturing where each method is first called is on the `JittingStarted` event.

---

## Module inference

Module name is resolved in order:

1. `ModuleILPath` payload → filename without extension (e.g. `MyApp.Services`)
2. `ModuleILFileName` alternate payload field
3. **Namespace inference** — first two dot-separated segments of `MethodNamespace` (e.g. `Microsoft.EntityFrameworkCore` from a fully-qualified EF type). This ensures methods always have a useful module label even when path payloads are absent (common in `.nettrace` files).

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `-n, --top <n>` | Top N items per table (default: 30) |
| `--process <name>` | Filter to a specific process name |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## Usage

```bash
DumpDetective jit-trace app.nettrace
DumpDetective jit-trace perf.etl --process w3wp --top 40
DumpDetective jit-trace app.nettrace --output jit-report.html
```

---

## Collecting compatible traces

JIT timing requires `Method/LoadVerbose` events. These are **not** in default profiles — you must explicitly request verbose method events:

```bash
# dotnet-trace
dotnet trace collect --providers 'Microsoft-Windows-DotNETRuntime:0x10:5' -p <pid>

# PerfView
PerfView.exe /ClrEvents:JITSymbols,Compilation,JitTracing,Default /JITInlining /NoGui collect
```

If only method counts (not timing) are needed, `Method/Load` events from any standard profile are sufficient.

---

## Interpreting results

| Signal | Likely cause | Action |
|---|---|---|
| High total JIT time (> 5 s) | Cold start / warm-up cost from large or dynamic codebase | Publish with ReadyToRun (`dotnet publish -r <rid>`) to pre-compile hot assemblies |
| Methods compiled > 1× | Dynamic code generation (`Expression.Compile`, `Emit`, re-JIT) | Compile once and cache; avoid per-request expression compilation |
| Single method > 200 ms | Extremely large method or deeply generic instantiation | Split large methods; reduce generic depth |
| High count from `Microsoft.EntityFrameworkCore` | EF Core compiles query expressions on first use | Pre-warm queries at startup; use compiled queries |

---

## No data?

If no JIT events appear, the trace was collected without verbose method events. Re-collect with:

```bash
# dotnet-trace
dotnet trace collect --providers 'Microsoft-Windows-DotNETRuntime:0x10:5' -p <pid>

# PerfView
PerfView.exe /ClrEvents:JITSymbols,Compilation,JitTracing,Default /JITInlining /NoGui collect
```
