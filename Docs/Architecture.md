# Architecture

DumpDetective is a .NET 10 native AOT CLI tool organized as a multi-project solution with a strict unidirectional dependency graph. This document describes the project layout, key abstractions, data-flow pipelines, and extension points.

---

## Solution Layout

```
DumpDetective.slnx

DumpDetective.Core/              ← interfaces, models, shared utilities
DumpDetective.Analysis/          ← stub project (references Analysis.Memory)
DumpDetective.Analysis.Memory/   ← ClrMD heap walk, DumpCollector, analyzers, consumers
DumpDetective.Reporting/         ← IRenderSink implementations, ReportDoc replay
DumpDetective.Commands/          ← ICommand implementations (Memory/ and Trace/)
DumpDetective.Cli/               ← AOT entry point, CommandRegistry, PluginLoader
DumpDetective.DiagnosticScenarios/ ← self-dump integration scenarios
DumpDetective.Tests/             ← integration test project
```

---

## Dependency Graph

```
Cli ──────────────────────────► Commands
 │                                  │
 │                                  ▼
 │                            Analysis.Memory ──────┐
 │                                                   │
 └──────────────► Reporting ──────► Core ◄───────────┘
```

- **Core** depends on nothing in the solution.
- **Analysis.Memory** and **Reporting** each depend only on Core.
- **Commands** depends on Core + Analysis.Memory + Reporting.
- **Cli** depends on Commands (everything else is transitive).
- **Tests** references Core + Analysis.Memory + Reporting + Commands directly.

---

## Key Abstractions

### `ICommand` (Core/Interfaces)

The single registration contract for every analysis command.

```csharp
public interface ICommand
{
    string      Name                 { get; }  // CLI sub-command name
    string      Description          { get; }  // one-line help text
    bool        IncludeInFullAnalyze { get; }  // included in `analyze --full`
    string      Category             { get; }  // help-panel grouping header
    CommandKind Kind                 { get; }  // Memory | Trace
    int         Run(string[] args);            // CLI entry point
    void        Render(DumpContext ctx, IRenderSink sink);
}
```

`CommandRegistry` in the Cli project holds the single source of truth: one entry per command in a private array. `Program.cs` dispatches via `CommandRegistry.Find(args[0])`. `AnalyzeCommand` iterates `CommandRegistry.FullAnalyzeCommands`.

### `IRenderSink` (Core/Interfaces)

Format-agnostic output abstraction. Every command writes to an `IRenderSink` — it never calls `Console.WriteLine` or constructs HTML strings directly.

| Sink | Output |
|---|---|
| `ConsoleSink` | Rich ANSI terminal output via Spectre.Console |
| `HtmlSink` | Self-contained HTML with inline CSS/JS, sticky nav, virtual scroll |
| `MarkdownSink` | GitHub-flavored Markdown |
| `TextSink` | Plain text |
| `JsonSink` | JSON array of report elements |
| `BinSink` | Binary-serialized `ReportDoc` for `render` and `diff` replay |
| `CaptureSink` | In-memory capture of all sink calls (used by `analyze --full` parallel workers) |

`SinkFactory.Create(outputPath)` selects the appropriate sink from the file extension. Multiple output paths are supported via `TeeRenderSink`.

### `IHeapObjectConsumer` (Core/Interfaces)

Single-responsibility accumulator registered with `HeapWalker.Walk`. Each implementation is called once per live heap object during the single `heap.EnumerateObjects()` pass.

```csharp
public interface IHeapObjectConsumer
{
    void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap);
    void OnWalkComplete();
    bool ConsumeFreeObjects { get; }  // default false
    bool IsThreadSafe       { get; }  // default false — use ConcurrentDictionary if true
    IHeapObjectConsumer CreateClone();
    void MergeFrom(IHeapObjectConsumer other);
}
```

**Performance contract**: `Consume` must not allocate per call. Use `CollectionsMarshal.GetValueRefOrAddDefault` for dictionary updates.

---

## Heap Walk Pipeline

`HeapWalker.Walk` calls `heap.EnumerateObjects()` exactly once per analysis run. Adding a new heap metric requires adding a new `IHeapObjectConsumer` — not editing the walker.

```
heap.EnumerateObjects()
        │
        ▼
HeapWalker.Walk(consumers: [TypeStatsConsumer, InboundRefConsumer, StringGroupConsumer,
                             GenCounterConsumer, ExceptionCountConsumer, AsyncMethodConsumer,
                             TimerConsumer, LargeObjectsConsumer, FragmentationConsumer,
                             ThreadPoolConsumer, HttpRequestsConsumer, CachePatternsConsumer,
                             ClosureCaptureConsumer, DataTableConsumer, ...])
        │
        │  Parallel over 8 segment buckets (LPT-ordered, largest segments first)
        │  Thread-unsafe consumers: cloned per bucket, merged after all buckets complete
        │  Thread-safe consumers (IsThreadSafe=true): shared instance, lock-free writes
        │
        ▼
consumers[i].OnWalkComplete()   ← called for every consumer after walk (even on throw)
        │
        ▼
HeapSnapshot populated ─────────────────────────────────────────────────────────────────
  • TypeStats:     Dictionary<ulong MT, TypeAgg>
  • InboundCounts: Dictionary<ulong addr, int refCount>
  • StringGroups:  Dictionary<string value, (count, totalBytes)>
  • Gen counters, LOH size, free bytes, exception counts, ...
```

`HeapSnapshot` is then shared read-only by all analyzers that run on the same `DumpContext`. Analyzers do not start secondary heap walks; they read from the snapshot.

---

## Memory Analysis Data Flow

```
DumpFile (.dmp / .mdmp)
    │
    ▼
DumpContext.OpenDump()       ← DataTarget.LoadDump + ClrRuntime.Create
    │
    ▼
DumpCollector.CollectFull()
    │  Runs HeapWalker with all consumers → populates HeapSnapshot
    │  Runs RuntimeSubCollectors (threads, handles, sync blocks)
    │  Calls HealthScorer.Score(snapshot, thresholds) → Findings + score
    │
    ▼
DumpSnapshot (POCO, AOT-JSON-serializable)
    │                                      Saved to .ddcache/snapshot.json
    ▼
Analyzer.Analyze(ctx)        ← reads ctx.Snapshot (pre-built) or walks heap if not available
    │
    ▼
Report data POCOs (in Core/Models/CommandData/)
    │
    ▼
Report.Render(data, IRenderSink)  ← in DumpDetective.Reporting/Reports/
    │
    ▼
IRenderSink impl → file / console
```

### Cache Lifecycle

Running `load` pre-builds all caches and saves them to a `.ddcache/` directory alongside the dump file. Subsequent commands skip the heap walk entirely and read from cache. `close` deletes the `.ddcache/` directory.

Cache files:

| File | Contents |
|---|---|
| `snapshot.json` | Full `DumpSnapshot` POCO |
| `bfs-index.bin` | BFS retained-size index (used by `static-refs`, `gc-roots`) |
| `bfs-parents.bin` | Parent-map for GC root chain tracing |
| `staticroots.bin` | Static reference roots cache |
| `gcroots.bin` | Full GC roots enumeration |

---

## Trace Analysis Data Flow

```
TraceFile (.nettrace / .etl)
    │
    ▼
TraceOpener.Open(path)       ← TraceLog.OpenOrConvert (TraceEvent)
    │
    ▼
ITraceSubAnalyzer.Analyze(traceLog, ctx)   ← 29 sub-analyzers run in TraceAnalyzeCommand
    │  Each sub-analyzer reads specific event names from the unified TraceLog
    │  Sub-analyzers are independent; trace-analyze opens the file once and fans out
    │
    ▼
Trace data POCOs (in Core/Models/CommandData/)
    │
    ▼
TraceReport.Render(data, IRenderSink)
    │
    ▼
IRenderSink impl → file / console
```

For `trace-dump-analyze`, both pipelines run in parallel and their outputs are passed to `CorrelationEngine.Correlate()` before rendering.

---

## `analyze --full` Parallel Execution

`AnalyzeCommand` runs all `IncludeInFullAnalyze = true` commands in parallel using `Parallel.For` with an LPT (Longest Processing Time) schedule — the slowest commands occupy the first 8 slots so all workers are busy from t=0.

Each parallel worker:
1. Creates a `CaptureSink` — records all `IRenderSink` calls as a `ReportDoc` in memory.
2. Calls `command.Render(ctx, captureSink)`.
3. Stores the captured `ReportDoc`.

After all workers finish, the main thread replays each `ReportDoc` into the final output sink in a deterministic order (matching the registry order). This means the output is always in the same order regardless of which command finished first.

```
analyze --full
    │
    ├── [wave 1, t=0]  StaticRefs, HeapFragmentation, LargeObjects,
    │                  MemoryLeak, HighRefs, EventAnalysis, ThreadAnalysis, WeakRefs
    │
    ├── [wave 2+]      ThreadPool, Deadlock, HttpRequests, ...all others...
    │
    └── [main thread]  Replay CaptureSink outputs in registry order → final IRenderSink
```

---

## Output Formats and `ReportDoc`

`ReportDoc` is a polymorphic POCO tree that represents a fully-rendered report. It is the serializable form used by `BinSink` / `render` / `diff`.

```
ReportDoc
  └── ReportChapter[]
        └── ReportSection[]
              └── ReportElement[]  (Table | Alert | Text | KeyValues | CallTree | Gauges | ...)
```

`ReportDocReplay.Replay(doc, sink)` feeds a `ReportDoc` back through any `IRenderSink` — this is how `render` converts a saved report to a new format.

`ReportDiffer.Diff(a, b)` computes element-level deltas between two `ReportDoc` instances — this is how `diff` compares two snapshots.

---

## Health Scoring

`HealthScorer.Score(DumpSnapshot, ScoringThresholds)` evaluates thresholds against plain POCO data — no ClrMD types, no dump file required. This makes it unit-testable in isolation.

The score starts at 100 and deducts points per finding. Thresholds are loaded from `dd-thresholds.json` (optional; falls back to compiled defaults via `ThresholdLoader`).

| Finding severity | Score deduction range |
|---|---|
| Info | 0 |
| Warning | 5–15 |
| Critical | 20–40 |

---

## Plugin System

Plugins are .NET assemblies placed in a directory listed in `PluginRoots.xml`. `PluginLoader` scans each directory for types implementing `ICommand` or `IPluginManifest`. Plugin commands are registered in `CommandRegistry` after built-in commands; name conflicts are silently dropped in favour of built-ins.

Trace plugins additionally implement `ITraceSubAnalyzer` to participate in `trace-analyze` and `ITraceDumpCorrelationRule` to participate in `trace-dump-analyze` correlation when `--with-plugins` is specified.

See [Plugins.md](Plugins.md) for authoring instructions.

---

## AOT and JSON

The tool publishes as a native AOT binary. All JSON serialization uses `[JsonSerializable]` source-gen contexts:

| Context | Types |
|---|---|
| `CoreJsonContext` | `DumpSnapshot`, `Finding`, `ReportDoc` tree, `ThresholdConfig` |

No `JsonSerializer.Serialize<T>` with runtime-generic overloads. No reflection (`Type.GetMethod`, `Activator.CreateInstance`, `Assembly.GetTypes`). All `CommandData` POCOs in `Core/Models/CommandData/` are registered in the appropriate source-gen context.

---

## Technology Stack

| Component | Package / Version |
|---|---|
| Framework | `net10.0`, `PublishAot=true`, `InvariantGlobalization=true` |
| Dump analysis | `Microsoft.Diagnostics.Runtime` 3.1.x (ClrMD) |
| Trace analysis | `Microsoft.Diagnostics.Tracing.TraceEvent` 3.1.x |
| Terminal UI | `Spectre.Console` 0.55.x |
| JSON | `System.Text.Json` (built-in, AOT source-gen only) |
