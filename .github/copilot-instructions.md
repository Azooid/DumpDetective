# GitHub Copilot Instructions — DumpDetective Workspace

## Project Context

This workspace contains two things:

1. **`DumpDetective/`** — The original, working implementation. **Do not touch this folder.** (See the hard rule at the bottom of this file.)
2. **`Docs/SRS-Redesign.md`** — A detailed Software Requirements Specification for a ground-up redesign. **All new code you write must follow this SRS.**

---

## What This Project Is

DumpDetective is a .NET 10 native AOT CLI tool that analyzes Windows memory dumps (`.dmp` / `.mdmp` files) using the ClrMD library (`Microsoft.Diagnostics.Runtime`). It runs 31 analysis commands — heap statistics, deadlock detection, thread analysis, memory leak detection, GC fragmentation, async backlog, finalizer queues, etc. — and produces output in HTML, Markdown, JSON, plain text, or rich console formats.

The redesigned project must do **everything the original does**, with the structural improvements specified in the SRS.

---

## SRS Summary — What the Redesign Must Implement

Full details are in `Docs/SRS-Redesign.md`. This section is a concise reference for Copilot.

### Technology Stack (unchanged from original)

| Item | Value |
|---|---|
| Framework | `net10.0` |
| AOT | `PublishAot=true`, `InvariantGlobalization=true` |
| ClrMD | `Microsoft.Diagnostics.Runtime` 3.1.x |
| Trace events | `Microsoft.Diagnostics.Tracing.TraceEvent` 3.1.x |
| Terminal UI | `Spectre.Console` 0.55.x |
| JSON | `System.Text.Json` with AOT source-gen contexts only — no runtime reflection |

---

### Improvement 1 — Command Registry (replaces dual hardcoded lists)

**Problem**: In the original, every new command must be registered in two places: `Program.cs` (a `switch` expression) and `AnalyzeCommand.RenderEmbeddedReports` (a 23-item `(label, Action)` array). Missing one silently breaks either standalone or full-analyze mode.

**Required design**:

```csharp
// Core/ICommand.cs
public interface ICommand
{
    string Name { get; }                    // CLI name, e.g. "heap-stats"
    string Description { get; }            // one-line help text
    bool IncludeInFullAnalyze { get; }      // included in `analyze --full`
    int Run(string[] args);                 // CLI entry point
    void Render(DumpContext ctx, IRenderSink sink);  // reusable renderer
}

// Core/CommandRegistry.cs
public static class CommandRegistry
{
    public static ICommand? Find(string name);
    public static IEnumerable<ICommand> All { get; }
    public static IEnumerable<ICommand> FullAnalyzeCommands { get; }
}
```

`Program.cs` must dispatch via `CommandRegistry.Find(args[0])`. `AnalyzeCommand` must iterate `CommandRegistry.FullAnalyzeCommands`. Adding a command requires editing exactly one place: the registry's internal array.

---

### Improvement 2 — Document-First Output Pipeline

**Problem**: The original's `IRenderSink` is a streaming interface (method calls in sequence). Parallel sub-reports require a `CaptureSink` + `ReportDocReplay` shim. Every new sink method must be added in four places.

**Required design**:

Every command must implement:
```csharp
ReportDoc BuildReport(DumpContext ctx);
```

`ICommand.Render` has a default implementation:
```csharp
void Render(DumpContext ctx, IRenderSink sink)
    => ReportDocReplay.Replay(BuildReport(ctx), sink);
```

Commands may override `Render` for performance-critical streaming. The `CaptureSink` and `ReportDocReplay` classes remain but are no longer the primary mechanism — `AnalyzeCommand` collects `ReportDoc` objects directly from `BuildReport` in parallel, then replays them in order.

`IRenderSink` remains unchanged as the format abstraction.

---

### Improvement 3 — HealthScorer (extracted from DumpCollector)

**Problem**: `DumpCollector.GenerateFindings` (~150 lines) is policy evaluation, not data collection. It cannot be unit tested without a real dump file.

**Required design**:

```csharp
// Core/HealthScorer.cs
public static class HealthScorer
{
    public static (IReadOnlyList<Finding> Findings, int Score) Score(
        DumpSnapshot snap,
        ScoringThresholds thresholds);
}
```

- No ClrMD types — pure POCO inputs and outputs.
- `DumpCollector` calls `HealthScorer.Score` at the end of collection.
- `HealthScorer` must produce **identical results** to the original `GenerateFindings` for all inputs.

---

### Improvement 4 — Heap Walk Visitor Pipeline

**Problem**: `DumpCollector.CollectHeapObjectsCombined` is ~500 lines because it manually multiplexes N accumulators in one loop. Adding a new metric requires editing this method.

**Required design**:

```csharp
// Collectors/IHeapObjectConsumer.cs
public interface IHeapObjectConsumer
{
    void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap);
    void Finalize();   // called exactly once after the walk completes
}

// Collectors/HeapWalker.cs
public static class HeapWalker
{
    public static void Walk(ClrHeap heap, IReadOnlyList<IHeapObjectConsumer> consumers);
}
```

`HeapWalker.Walk` calls `heap.EnumerateObjects()` **once** regardless of how many consumers are registered. `Finalize()` is called on every consumer after the walk, even if the walk throws.

Required consumer classes (each ≤ 100 lines, in `Collectors/Consumers/`):

| Class | Accumulates |
|---|---|
| `TypeStatsConsumer` | `TypeAgg` dict for `HeapSnapshot.TypeStats` |
| `InboundRefConsumer` | `InboundCounts` dict for `HeapSnapshot` |
| `StringGroupConsumer` | `StringGroups` dict for `HeapSnapshot` |
| `GenCounterConsumer` | Gen0/1/2/LOH/POH totals and object counts |
| `ExceptionCountConsumer` | Live exception instances per type |
| `AsyncMethodConsumer` | Active async state machine count |
| `TimerConsumer` | Live `System.Threading.Timer` count |
| `WcfStateConsumer` | WCF channel state field reads |
| `ConnectionConsumer` | DB connection counts by state |
| `EventLeakConsumer` | Event field delegate list growth detection |

`DumpCollector.CollectHeapObjectsCombined` becomes a ~20-line orchestrator that instantiates consumers, calls `HeapWalker.Walk`, then reads results.

---

### Improvement 5 — Single Snapshot Model (eliminates SnapshotData)

**Problem**: `DumpSnapshot` uses `ValueTuple` fields (not AOT JSON-safe). `SnapshotData` is a structural mirror with `From()`/`ToSnapshot()` conversions. Any field change requires updating both classes.

**Required design**:

- `DumpSnapshot` must use **no `ValueTuple` fields** and **no anonymous types**.
- All collection element types must be named `record` types registered in an AOT `[JsonSerializable]` context.
- `SnapshotData.cs` must **not exist** in the redesign.
- `DumpSnapshot` is directly JSON-serializable via source-gen.
- `DumpSnapshot.SubReport` (formerly `SnapshotData.SubReport`) holds the captured `ReportDoc` for trend archiving.

---

### Improvement 6 — Test Project

**Required structure**:

```
DumpDetectiveV2.Tests/
  DumpDetectiveV2.Tests.csproj   (xUnit, net10.0, no AOT)
  HealthScorer/
    HealthScorerTests.cs
  Rendering/
    ReportDocReplayTests.cs
    HtmlSinkTests.cs
    MarkdownSinkTests.cs
  CLI/
    ArgumentParserTests.cs
  Thresholds/
    ThresholdLoaderTests.cs
  GoldenFiles/          (one .json golden ReportDoc per command)
  Fixtures/
    DumpFixture.cs      (skips integration tests if no .dmp available)
```

**Unit tests (no dump file)**: `HealthScorer`, `ThresholdLoader`, `DumpHelpers`, `CliArgs`, `ReportDocReplay`.

**Golden file tests (no dump file)**: Each command's `BuildReport` is called with a faked or pre-built `DumpContext`/`DumpSnapshot`; resulting `ReportDoc` is compared to a golden JSON file.

**Integration tests (optional, require a `.dmp`)**: Skipped if `DD_TEST_DUMP` env var is not set.

---

### Improvement 7 — Shared Argument Parser

**Problem**: All 31 commands hand-roll their own `args` parsing, causing inconsistent behavior.

**Required design**:

```csharp
// Core/CliArgs.cs  (≤ 100 lines)
public sealed class CliArgs
{
    public string? DumpPath { get; }
    public string? OutputPath { get; }
    public bool Help { get; }

    public bool HasFlag(string name);
    public string? GetOption(string name);
    public int GetInt(string name, int @default);
    public string GetString(string name, string @default);

    public static CliArgs Parse(string[] args);
}
```

Conventions:
- `--help` / `-h` always recognized.
- `--output` / `-o` always the output path.
- First non-flag positional: dump path (overridden by `DD_DUMP` env var if not present).
- Unknown flags: silently ignored.

All commands must use `CliArgs.Parse`.

---

### Improvement 8 — Structured Tooltip Metadata in HtmlSink

**Problem**: HtmlSink matches tooltips to sections using `string.Contains` on section titles. Renaming a section silently drops its tooltip.

**Required design**:

```csharp
// IRenderSink.Section gains an optional key
void Section(string title, string? sectionKey = null);
```

`HtmlSink` indexes tooltips by `sectionKey` when provided, with a fallback to title-matching for backward compatibility. All other sinks ignore `sectionKey`.

---

## Project Naming

The solution is named **`DumpDetectiveV2`**. The solution file is `DumpDetectiveV2.slnx`.  
There are **six projects**: one per layer plus the test project.  
Root namespace for every project: `DumpDetectiveV2.<ProjectSuffix>` (e.g. `DumpDetectiveV2.Core`, `DumpDetectiveV2.Analysis`).

---

## Multi-Project Solution Structure

```
DumpDetectiveV2.slnx                      ← solution file

DumpDetectiveV2.Core/                     ← models, interfaces, shared utilities
  DumpDetectiveV2.Core.csproj             ← net10.0 class library; NuGet: ClrMD, Spectre.Console, System.Text.Json
  Interfaces/
    ICommand.cs                           ← Name, Description, IncludeInFullAnalyze, Run, Render, BuildReport
    IRenderSink.cs                        ← Section(title, sectionKey?), Table, Alert, Header, etc.
    IHeapObjectConsumer.cs                ← Consume(in ClrObject, HeapTypeMeta, ClrHeap) + Finalize()
  Models/
    ReportDoc.cs                          ← ReportChapter, ReportSection, all ReportElement subtypes
    Finding.cs                            ← immutable record
    DumpSnapshot.cs                       ← NO ValueTuple fields; AOT-serializable directly
    ThresholdConfig.cs                    ← ScoringThresholds + TrendThresholds
  Runtime/
    DumpContext.cs                        ← ClrMD DataTarget + ClrRuntime; EnsureSnapshot / PreloadSnapshot
    HeapSnapshot.cs                       ← TypeStats, InboundCounts, StringGroups, gen counters
    HeapTypeMeta.cs                       ← per-MethodTable metadata cache entry
  Utilities/
    CliArgs.cs                            ← shared arg parser; ≤ 100 lines
    DumpHelpers.cs                        ← FormatSize, IsSystemType, IsExceptionType, OpenDump, SegmentKindLabel
    ThresholdLoader.cs                    ← lazy singleton; silent fallback to defaults
    HealthScorer.cs                       ← Score(DumpSnapshot, ScoringThresholds) → (Findings, score)
    CommandBase.cs                        ← Execute lifecycle, [ThreadStatic] SuppressVerbose, TryHelp, RunStatus
  Json/
    CoreJsonContext.cs                    ← [JsonSerializable] for DumpSnapshot, Finding, ReportDoc, ThresholdConfig

DumpDetectiveV2.Analysis/                ← all ClrMD data collection, heap walking
  DumpDetectiveV2.Analysis.csproj        ← net10.0 class library; NuGet: TraceEvent; Ref: Core
  DumpCollector.cs                       ← CollectFull, CollectLightweight, CollectThreads, CollectHandles, etc.
  HeapWalker.cs                          ← Walk(ClrHeap, consumers) — single EnumerateObjects call
  TrendRawSerializer.cs                  ← DumpSnapshot JSON storage for trend-analysis
  Consumers/
    TypeStatsConsumer.cs
    InboundRefConsumer.cs
    StringGroupConsumer.cs
    GenCounterConsumer.cs
    ExceptionCountConsumer.cs
    AsyncMethodConsumer.cs
    TimerConsumer.cs
    WcfStateConsumer.cs
    ConnectionConsumer.cs
    EventLeakConsumer.cs

DumpDetectiveV2.Reporting/               ← all output format implementations
  DumpDetectiveV2.Reporting.csproj       ← net10.0 class library; NuGet: (transitive only); Ref: Core
  Sinks/
    HtmlSink.cs                          ← self-contained HTML; inline CSS/JS; sticky nav; virtual scroll
    MarkdownSink.cs
    TextSink.cs
    ConsoleSink.cs
    JsonSink.cs
    CaptureSink.cs
  ReportDocReplay.cs                     ← Replay(ReportDoc, IRenderSink)
  ToolMemoryDiagnostic.cs

DumpDetectiveV2.Commands/                ← all 31 command implementations
  DumpDetectiveV2.Commands.csproj        ← net10.0 class library; Ref: Core, Analysis, Reporting
  AnalyzeCommand.cs                      ← orchestrator; iterates CommandRegistry.FullAnalyzeCommands
  HeapStatsCommand.cs
  GenSummaryCommand.cs
  HighRefsCommand.cs
  StringDuplicatesCommand.cs
  MemoryLeakCommand.cs
  HeapFragmentationCommand.cs
  LargeObjectsCommand.cs
  PinnedObjectsCommand.cs
  GcRootsCommand.cs
  FinalizerQueueCommand.cs
  HandleTableCommand.cs
  StaticRefsCommand.cs
  WeakRefsCommand.cs
  ThreadAnalysisCommand.cs
  ThreadPoolCommand.cs
  ThreadPoolStarvationCommand.cs
  DeadlockDetectionCommand.cs
  AsyncStacksCommand.cs
  ExceptionAnalysisCommand.cs
  EventAnalysisCommand.cs
  HttpRequestsCommand.cs
  ConnectionPoolCommand.cs
  WcfChannelsCommand.cs
  TimerLeaksCommand.cs
  TypeInstancesCommand.cs
  ObjectInspectCommand.cs
  ModuleListCommand.cs
  TrendAnalysisCommand.cs
  TrendRenderCommand.cs
  RenderCommand.cs

DumpDetectiveV2.Cli/                     ← entry point; the AOT executable
  DumpDetectiveV2.Cli.csproj             ← OutputType=Exe; PublishAot=true; InvariantGlobalization=true; Ref: Commands
  Program.cs                             ← top-level statements; ~10 lines
  CommandRegistry.cs                     ← static array of all ICommand instances; Find / All / FullAnalyzeCommands
  Properties/
    launchSettings.json
    PublishProfiles/
      FolderProfile.pubxml

DumpDetectiveV2.Tests/                   ← xUnit test project; no AOT
  DumpDetectiveV2.Tests.csproj           ← net10.0; xUnit; Ref: Core, Analysis, Reporting, Commands
  HealthScorer/
    HealthScorerTests.cs
  Rendering/
    ReportDocReplayTests.cs
    HtmlSinkTests.cs
    MarkdownSinkTests.cs
  CLI/
    ArgumentParserTests.cs
  Thresholds/
    ThresholdLoaderTests.cs
  GoldenFiles/                           ← one .json golden ReportDoc per command (31 files)
  Fixtures/
    DumpFixture.cs                       ← skips integration tests if DD_TEST_DUMP env var not set
  Integration/
    AllCommandsRunTest.cs                ← skipped without a real .dmp file

Docs/
  SRS-Redesign.md                        ← the full SRS (read this for details)
```

---

## Dependency Graph

```
Cli ──────────────────────────► Commands
 │                                  │
 │                                  ▼
 │                            Analysis ──────────┐
 │                                               │
 └──────────────► Reporting ──────► Core ◄───────┘
```

- **Core** has no dependencies on the other five projects.
- **Analysis** and **Reporting** each depend only on Core.
- **Commands** depends on Core + Analysis + Reporting.
- **Cli** depends on Commands (and gets everything transitively).
- **Tests** references Core + Analysis + Reporting + Commands directly.

---

## Package-to-Project Matrix

| NuGet Package | Core | Analysis | Reporting | Commands | Cli | Tests |
|---|:---:|:---:|:---:|:---:|:---:|:---:|
| `Microsoft.Diagnostics.Runtime` 3.1.x | ✓ | transitive | — | — | — | — |
| `Microsoft.Diagnostics.Tracing.TraceEvent` 3.1.x | — | ✓ | — | — | — | — |
| `Spectre.Console` 0.55.x | ✓ | — | transitive | — | — | — |
| `System.Text.Json` (built-in) | ✓ | — | transitive | — | — | — |
| `xunit` | — | — | — | — | — | ✓ |

---

## Coding Rules

1. **AOT-safe JSON only.** All JSON serialization uses `[JsonSerializable]` source-gen contexts. No `JsonSerializer.Serialize<T>` with runtime-generic overloads outside of a source-gen context.
2. **No runtime reflection.** No `Type.GetMethod`, `Activator.CreateInstance`, `Assembly.GetTypes`, etc.
3. **Hot loop allocations.** `IHeapObjectConsumer.Consume` must not allocate per call. Use `CollectionsMarshal.GetValueRefOrAddDefault` for dictionary updates in hot paths.
4. **Single heap walk.** `HeapWalker.Walk` calls `heap.EnumerateObjects()` exactly once. Never add a second walk for a single command's needs — add a new `IHeapObjectConsumer` instead.
5. **`[ThreadStatic] SuppressVerbose`.** Preserve this pattern in `CommandBase` for parallel sub-report workers.
6. **Error handling.** `CommandBase.Execute` catches `InvalidOperationException` (no CLR in dump → exit 1) and `Exception` (unexpected → exit 1). Commands do not catch exceptions themselves unless handling a specific expected case.
7. **Exit codes.** All commands return `0` on success, `1` on error.
8. **Spectre.Console.** Use `AnsiConsole` for all console output. No `Console.WriteLine` in command code.
9. **`DD_DUMP` env var.** `CliArgs.Parse` must inject the dump path from `DD_DUMP` if no positional `.dmp`/`.mdmp` argument is present.
10. **`dd-thresholds.json` format.** Unchanged from the original. The file is optional; missing or invalid files silently fall back to defaults.

---

## What to Copy vs. What to Redesign

### Copy as-is (logic is correct, just move to new namespace):
- All 5 `IRenderSink` format implementations (`HtmlSink`, `MarkdownSink`, `TextSink`, `ConsoleSink`, `CaptureSink`) — subject only to the `sectionKey` additive change in `Section()`
- `ReportDocReplay`
- `ThresholdLoader` (lazy singleton, AOT source-gen, silent fallback)
- `DumpHelpers` (`FormatSize`, `IsSystemType`, `IsExceptionType`, `OpenDump`, `SegmentKindLabel`)
- `DumpContext` (ClrMD wrapper, `EnsureSnapshot`/`PreloadSnapshot`)
- `HeapSnapshot` (cache model — populated by new `Consumers` instead of `CollectHeapObjectsCombined`)
- `Finding` record
- `ReportDoc` model (polymorphic JSON tree)
- `ThresholdConfig` POCOs
- `ToolMemoryDiagnostic`
- The `typeMetaCache` pattern from `DumpCollector` — move to `HeapWalker` as the shared `HeapTypeMeta` builder
- All 31 command analysis logic — extract analysis logic into `BuildReport` implementations

### Redesign (not copy):
- `Program.cs` → uses `CommandRegistry.Find`
- `DumpCollector.CollectHeapObjectsCombined` → becomes `HeapWalker` + 10 consumers
- `DumpCollector.GenerateFindings` → becomes `HealthScorer.Score`
- `DumpSnapshot` / `SnapshotData` → unified `DumpSnapshot` with no `ValueTuple` fields
- Every command → implements `ICommand`, uses `CliArgs.Parse`, implements `BuildReport`
- `AnalyzeCommand.RenderEmbeddedReports` → iterates `CommandRegistry.FullAnalyzeCommands`, collects `ReportDoc` from `BuildReport`

---

## Full List of Commands (all 31 must be implemented)

| Command Name | CLI Flag | Full-Analyze |
|---|---|---|
| `analyze` | — | N/A (orchestrator) |
| `heap-stats` | — | Yes |
| `gen-summary` | — | Yes |
| `high-refs` | — | Yes |
| `string-duplicates` | — | Yes |
| `memory-leak` | — | Yes |
| `heap-fragmentation` | — | Yes |
| `large-objects` | — | Yes |
| `pinned-objects` | — | Yes |
| `gc-roots` | — | No (too slow for full-analyze) |
| `finalizer-queue` | — | Yes |
| `handle-table` | — | Yes |
| `static-refs` | — | Yes |
| `weak-refs` | — | Yes |
| `thread-analysis` | — | Yes |
| `thread-pool` | — | Yes |
| `thread-pool-starvation` | — | Yes |
| `deadlock-detection` | — | Yes |
| `async-stacks` | — | Yes |
| `exception-analysis` | — | Yes |
| `event-analysis` | — | Yes |
| `http-requests` | — | Yes |
| `connection-pool` | — | Yes |
| `wcf-channels` | — | Yes |
| `timer-leaks` | — | Yes |
| `type-instances` | — | No (requires `--type` arg) |
| `object-inspect` | — | No (requires `--address` arg) |
| `module-list` | — | Yes |
| `trend-analysis` | — | No (multi-dump) |
| `trend-render` | — | No (replay only) |
| `render` | — | No (replay only) |

---

## HARD RULE — DO NOT TOUCH THE `DumpDetective/` FOLDER

The `DumpDetective/` folder and all files beneath it are **read-only reference material**. This includes:

- `DumpDetective/DumpDetective.csproj`
- `DumpDetective/Program.cs`
- `DumpDetective/Core/**`
- `DumpDetective/Collectors/**`
- `DumpDetective/Commands/**`
- `DumpDetective/Models/**`
- `DumpDetective/Output/**`
- `DumpDetective/Helpers/**`
- `DumpDetective/Properties/**`
- `DumpDetective.slnx`

**Do not create, edit, delete, or rename any file inside `DumpDetective/`.** Do not add the original `DumpDetective` project to any new solution file. You may **read** files in `DumpDetective/` to understand existing logic before reimplementing it in `DumpDetectiveV2/`.

All new code goes into `DumpDetectiveV2/`, `DumpDetectiveV2.Tests/`, or `Docs/`.
