# Software Requirements Specification — DumpDetective Redesign
**Version**: 2.0  
**Date**: April 17, 2026  
**Scope**: Architectural improvements derived from a full codebase audit of the existing DumpDetective tool. Includes the full multi-project solution design.

---

## Table of Contents

1. [Introduction](#1-introduction)
2. [Current Architecture Overview](#2-current-architecture-overview)
3. [Identified Problems](#3-identified-problems)
4. [Multi-Project Solution Design](#4-multi-project-solution-design)
5. [Proposed Architecture — Detail](#5-proposed-architecture--detail)
6. [Functional Requirements](#6-functional-requirements)
7. [Non-Functional Requirements](#7-non-functional-requirements)
8. [Components to Preserve Unchanged](#8-components-to-preserve-unchanged)
9. [Migration Path](#9-migration-path)
10. [Glossary](#10-glossary)

---

## 1. Introduction

### 1.1 Purpose

This document specifies the requirements and rationale for a ground-up redesign of DumpDetective — a .NET memory dump analysis CLI tool built on ClrMD. The goal is not to change what the tool does, but to address structural friction points that make the codebase harder to extend, test, and maintain.

### 1.2 Background

DumpDetective is a native AOT .NET 10 tool that accepts `.dmp`/`.mdmp` files and runs 31 analysis commands against them, producing output in HTML, Markdown, JSON, plain text, or console formats. A full analyze mode runs 23 sub-commands in parallel and aggregates their output into a single report.

The tool works correctly and performs well. The issues identified below are entirely about maintainability, extensibility, and testability — not correctness.

### 1.3 Scope of This Document

This SRS covers:
- The eight architectural problems identified in the audit
- Concrete requirements for the redesigned system
- What must be preserved from the current implementation
- A suggested migration path that avoids a big-bang rewrite

### 1.4 Technology Constraints (unchanged)

- Target: `net10.0`, `PublishAot=true`, `InvariantGlobalization=true`
- Dependencies: `Microsoft.Diagnostics.Runtime`, `Microsoft.Diagnostics.Tracing.TraceEvent`, `Spectre.Console`
- All JSON serialization must use AOT source-gen contexts (`[JsonSerializable]`)
- No runtime reflection

---

## 2. Current Architecture Overview

### 2.1 Project Structure

```
DumpDetective/
  Program.cs               — CLI router (switch expression over args[0])
  Core/
    CommandBase.cs         — Static utility: execute lifecycle, arg parsing, spinner
    DumpContext.cs         — ClrMD DataTarget + ClrRuntime wrapper; HeapSnapshot cache
    HeapSnapshot.cs        — Cached heap walk results (TypeStats, InboundCounts, StringGroups, gen counters)
    ThresholdLoader.cs     — Lazy singleton; loads dd-thresholds.json with fallback to defaults
  Collectors/
    DumpCollector.cs       — All heap walking and data aggregation; GenerateFindings
  Commands/
    AnalyzeCommand.cs      — Orchestrator for mini/full mode; 23-item parallel sub-report array
    HeapStatsCommand.cs    — (x31 more command files, same pattern)
  Models/
    DumpSnapshot.cs        — Live analysis DTO (uses ValueTuples)
    SnapshotData.cs        — AOT-safe serializable mirror of DumpSnapshot; SubReport field
    Finding.cs             — Immutable record: severity, category, headline, detail, advice, deduction
    ReportDoc.cs           — Serializable document tree: Chapter → Section → ReportElement subtypes
    ThresholdConfig.cs     — ScoringThresholds + TrendThresholds POCOs
  Output/
    IRenderSink.cs         — 12-method streaming output interface
    HtmlSink.cs            — Self-contained HTML with inline CSS/JS
    MarkdownSink.cs        — GFM output
    JsonSink.cs            — Delegates to CaptureSink; serializes ReportDoc on Dispose
    TextSink.cs            — ASCII-aligned plain text
    ConsoleSink.cs         — Spectre.Console rich terminal
    CaptureSink.cs         — In-memory ReportDoc builder (used for parallel buffering)
  Helpers/
    DumpHelpers.cs         — FormatSize, IsSystemType, IsExceptionType, OpenDump, SegmentKindLabel
    ReportDocReplay.cs     — Walks ReportDoc tree and replays into any IRenderSink
    ToolMemoryDiagnostic.cs
```

### 2.2 Data Flow (current)

```
dump.dmp
  └─ DumpContext.Open()
       └─ DumpCollector.CollectFull(ctx)
            ├─ CollectThreads / CollectHandles / CollectModules / CollectSegmentLayout
            ├─ CollectHeapObjectsCombined()   ← single EnumerateObjects pass
            │    builds: DumpSnapshot fields + HeapSnapshot (TypeAgg, InboundCounts, etc.)
            │    ctx.PreloadSnapshot(snap)
            └─ GenerateFindings()             ← threshold evaluation → Finding list + score
  └─ IRenderSink.Create(outputPath)
       └─ AnalyzeCommand.RenderReport(snap, sink)        ← summary
            └─ RenderEmbeddedReports(ctx, sink)          ← full mode
                 ├─ Parallel.For(23 sub-commands, MaxDop=8)
                 │    each → XxxCommand.Render(ctx, CaptureSink[i])
                 └─ ReportDocReplay.Replay(captures[i].Doc, realSink)  ← in-order replay
```

### 2.3 Key Performance Optimizations Already In Place

- **Single combined heap walk**: `CollectHeapObjectsCombined` fills both `DumpSnapshot` and `HeapSnapshot` in one `EnumerateObjects` pass. All 23 sub-commands that need type statistics hit the cached `HeapSnapshot.TypeStats` dictionary instead of re-walking.
- **`typeMetaCache`**: Per-MethodTable metadata cached to avoid repeated ClrMD field enumeration for objects of the same type.
- **`CollectionsMarshal.GetValueRefOrAddDefault`**: Hot loop dict updates avoid double-lookup.
- **`[ThreadStatic] SuppressVerbose`**: Parallel sub-report workers suppress Spectre.Console output without locks.

---

## 3. Identified Problems

### P1 — Dual Command Registration (High Impact)

**Where**: `Program.cs` (switch router) + `AnalyzeCommand.RenderEmbeddedReports` (23-item array).

**Problem**: Every new command must be registered in two places. Missing one registration silently drops the command from either standalone or full-analyze mode. There is no compile-time enforcement that a command present in one list is also present in the other.

**Effect**: Maintenance burden scales linearly with the number of commands.

---

### P2 — Streaming-First Sink Requires Buffering Shim (Medium Impact)

**Where**: `IRenderSink`, `CaptureSink`, `ReportDocReplay`.

**Problem**: The primary output abstraction is a streaming method-call interface. Because `AnalyzeCommand.RenderEmbeddedReports` runs 23 sub-commands in parallel and must emit them in original order, output must be buffered into 23 `CaptureSink` instances and then replayed via `ReportDocReplay` into the real sink.

The `ReportDoc` document model already exists for exactly this purpose (and for JSON archiving), but it is not the primary output path — it is a bolt-on buffering layer. This results in two parallel output abstractions that must stay in sync.

**Effect**: Any new `IRenderSink` method also requires a `CaptureSink` case, a `ReportDocReplay` case, and a new `ReportElement` subtype. Four places to update for one new capability.

---

### P3 — Health Scoring Logic Inside DumpCollector (Medium Impact)

**Where**: `DumpCollector.GenerateFindings` (~150 lines).

**Problem**: `DumpCollector` is responsible for data collection (heap walking, thread enumeration, handle collection). `GenerateFindings` is policy evaluation — it reads a `DumpSnapshot` and a `ThresholdConfig` and produces `Finding` records. These are different concerns and different change reasons.

**Effect**: `DumpCollector` is harder to unit test (requires a real dump to exercise scoring logic). Scoring rules cannot be modified or mocked independently of the collector.

---

### P4 — Monolithic Heap Walk Method (Medium Impact)

**Where**: `DumpCollector.CollectHeapObjectsCombined` (~500 lines).

**Problem**: The method is an accumulator multiplex — it manually threads N different accumulators through a single `EnumerateObjects` loop. Adding a new metric requires editing this method, adding more branches to the hot path, and understanding the full context of all existing accumulators.

**Effect**: The method is one of the hardest in the codebase to reason about and test in isolation. Adding a feature (e.g., tracking a new object type) requires understanding and modifying a 500-line hot loop.

---

### P5 — DumpSnapshot / SnapshotData Duality (Medium Impact)

**Where**: `DumpSnapshot.cs` + `SnapshotData.cs`.

**Problem**: `DumpSnapshot` uses `ValueTuple` fields (convenient internally, not AOT JSON-serializable). `SnapshotData` is a structural copy of `DumpSnapshot` with named record types instead of value tuples, plus `From()`/`ToSnapshot()` conversion methods. Any change to `DumpSnapshot` must be mirrored in `SnapshotData`.

**Effect**: Parallel class evolution; any new field added to `DumpSnapshot` requires a matching addition in `SnapshotData` and updates to both conversion methods.

---

### P6 — No Test Project (High Impact)

**Where**: The solution has no test project.

**Problem**: There are no automated tests for any component. The `GenerateFindings` logic, `ThresholdLoader`, `DumpHelpers`, `ReportDocReplay`, and all rendering pipeline code are untested. The only verification is a manual `dotnet build` + manual test run.

**Effect**: Regressions in scoring logic, rendering, or argument parsing are only caught by manual testing. Refactoring any component carries unmeasured risk.

---

### P7 — Per-Command Argument Parsing (Low Impact)

**Where**: Every command's `Run(string[] args)` method.

**Problem**: All 31 commands hand-roll their own argument parsing loops. This produces inconsistent behavior (some commands check `--help` early, some don't; some accept `-o` and `--output`, others only one form) and amounts to several hundred lines of duplicated string wrangling.

**Effect**: Inconsistent CLI UX. Fixing a parsing bug requires finding and fixing it in multiple commands.

---

### P8 — HtmlSink Tooltip Coupling (Low Impact)

**Where**: `HtmlSink.cs`, ~100 string-matching tooltip entries.

**Problem**: Tooltips are associated with section titles by `string.Contains` matching. If a section title changes, its tooltip silently disappears at runtime. There is no compile-time warning.

**Effect**: Renamed sections lose their tooltips invisibly.

---

## 4. Multi-Project Solution Design

### 4.1 Solution Structure

The redesign splits the single-project original into **six focused projects**. Each project has one responsibility, one set of dependencies, and can be compiled, tested, and reasoned about independently.

```
DumpDetectiveV2.slnx

DumpDetectiveV2.Core/
DumpDetectiveV2.Analysis/
DumpDetectiveV2.Reporting/
DumpDetectiveV2.Commands/
DumpDetectiveV2.Cli/
DumpDetectiveV2.Tests/

Docs/
  SRS-Redesign.md
```

### 4.2 Dependency Graph

```
Cli ──────────────────────────► Commands
 │                                  │
 │                                  ▼
 │                            Analysis ──────────┐
 │                                               │
 └──────────────► Reporting ──────► Core ◄───────┘
```

**Rules:**
- **Core** has zero dependencies on the other five projects.
- **Analysis** and **Reporting** each depend only on Core.
- **Commands** depends on Core + Analysis + Reporting.
- **Cli** depends on Commands (and gets Core + Analysis + Reporting transitively).
- **Tests** references Core + Analysis + Reporting + Commands directly.

### 4.3 Project: `DumpDetectiveV2.Core`

**Role**: Every other project depends on this one. Contains all models, interfaces, and shared utilities. This is the only place where the fundamental contracts (`ICommand`, `IRenderSink`, `IHeapObjectConsumer`, `DumpContext`) are defined.

**Why ClrMD lives in Core**: `ICommand.Render(DumpContext, IRenderSink)` and `IHeapObjectConsumer.Consume(in ClrObject, HeapTypeMeta, ClrHeap)` are interfaces that all downstream projects implement. Both expose ClrMD types in their signatures. Abstracting them behind a `IDumpContext` wrapper would add a layer of indirection with no practical benefit — this tool's sole purpose is ClrMD dump analysis.

**NuGet packages**:

| Package | Reason |
|---|---|
| `Microsoft.Diagnostics.Runtime` 3.1.x | `DumpContext`, `IHeapObjectConsumer`, `HeapTypeMeta` all touch ClrMD types |
| `Spectre.Console` 0.55.x | `CommandBase` uses `AnsiConsole` for the spinner and error output |
| `System.Text.Json` (built-in) | `ThresholdLoader` deserializes `dd-thresholds.json`; `CoreJsonContext` AOT source-gen |

**File layout**:
```
DumpDetectiveV2.Core/
  DumpDetectiveV2.Core.csproj
  Interfaces/
    ICommand.cs              ← Name, Description, IncludeInFullAnalyze, Run(string[]), Render(DumpContext, IRenderSink), BuildReport(DumpContext)
    IRenderSink.cs           ← Section(title, sectionKey?), Table, Alert, Header, BeginDetails, EndDetails, etc.; static Create factory
    IHeapObjectConsumer.cs   ← Consume(in ClrObject, HeapTypeMeta, ClrHeap); Finalize()
  Models/
    ReportDoc.cs             ← ReportChapter → ReportSection → ReportElement subtypes (polymorphic [JsonDerivedType])
    Finding.cs               ← immutable record: Severity, Category, Headline, Detail, Advice, Deduction
    DumpSnapshot.cs          ← all analysed dump data; NO ValueTuple fields; AOT-serializable via CoreJsonContext
    ThresholdConfig.cs       ← ScoringThresholds + TrendThresholds POCOs with defaults
  Runtime/
    DumpContext.cs           ← DataTarget + ClrRuntime; Open(path); EnsureSnapshot(); PreloadSnapshot(snap); Dispose
    HeapSnapshot.cs          ← TypeAgg dict; InboundCounts; StringGroups; gen totals; Build(ctx) + Create(...) factory
    HeapTypeMeta.cs          ← per-MethodTable cache entry: Name, IsException, IsAsync, IsTimer, IsWcf, IsConnection, DelegateFields[]
  Utilities/
    CliArgs.cs               ← Parse(string[]); DumpPath, OutputPath, Help; HasFlag, GetOption, GetInt, GetString; DD_DUMP injection; ≤ 100 lines
    DumpHelpers.cs           ← FormatSize, IsSystemType, IsExceptionType(ClrType), OpenDump, SegmentKindLabel
    ThresholdLoader.cs       ← static Current { get; }; lazy singleton; reads dd-thresholds.json; silent fallback to ThresholdConfig defaults
    HealthScorer.cs          ← Score(DumpSnapshot, ScoringThresholds) → (IReadOnlyList<Finding>, int score); no ClrMD types; fully unit-testable
    CommandBase.cs           ← Execute(dumpPath, outputPath, body); ParseCommon; TryHelp; RunStatus(msg, body); [ThreadStatic] SuppressVerbose; EffectiveTop; PrintAnalyzing
  Json/
    CoreJsonContext.cs       ← [JsonSerializable(typeof(DumpSnapshot))], [JsonSerializable(typeof(Finding))], [JsonSerializable(typeof(ReportDoc))], [JsonSerializable(typeof(ThresholdConfig))]
```

### 4.4 Project: `DumpDetectiveV2.Analysis`

**Role**: Every line of code that opens a managed heap or reads `.nettrace` event data. Nothing about rendering or CLI parsing lives here.

**Why separate**: `Microsoft.Diagnostics.Tracing.TraceEvent` is only required by `ThreadPoolStarvationCommand`'s `.nettrace` parsing. Isolating it here means Core, Reporting, Commands, and Cli never pull in that large package.

**NuGet packages**:

| Package | Reason |
|---|---|
| `Microsoft.Diagnostics.Tracing.TraceEvent` 3.1.x | `.nettrace` parsing for thread-pool starvation detection |
| `Microsoft.Diagnostics.Runtime` 3.1.x | transitive from Core reference |

**File layout**:
```
DumpDetectiveV2.Analysis/
  DumpDetectiveV2.Analysis.csproj   (Ref: Core)
  DumpCollector.cs                  ← CollectFull(ctx), CollectLightweight(ctx), CollectThreads, CollectThreadPool, CollectHandles, CollectModules, CollectSegmentLayout, CollectFinalizerQueue; calls HealthScorer.Score
  HeapWalker.cs                     ← Walk(ClrHeap, IReadOnlyList<IHeapObjectConsumer>); builds typeMetaCache; single EnumerateObjects(); calls Finalize() on all consumers even on throw
  TrendRawSerializer.cs             ← SaveSnapshot(DumpSnapshot, path); LoadSnapshot(path) → DumpSnapshot; uses AOT source-gen JSON
  Consumers/
    TypeStatsConsumer.cs            ← builds TypeAgg dict → HeapSnapshot.TypeStats; ≤ 100 lines
    InboundRefConsumer.cs           ← counts EnumerateReferenceAddresses per object → HeapSnapshot.InboundCounts; ≤ 100 lines
    StringGroupConsumer.cs          ← groups System.String values → HeapSnapshot.StringGroups; ≤ 100 lines
    GenCounterConsumer.cs           ← accumulates gen0/1/2/LOH/POH object count+size totals; ≤ 100 lines
    ExceptionCountConsumer.cs       ← counts live exception instances per type name; ≤ 100 lines
    AsyncMethodConsumer.cs          ← counts active async state machine objects; ≤ 100 lines
    TimerConsumer.cs                ← counts live System.Threading.Timer objects; ≤ 100 lines
    WcfStateConsumer.cs             ← reads WCF channel state int field via TryReadIntField; ≤ 100 lines
    ConnectionConsumer.cs           ← counts DB connection objects by state string; ≤ 100 lines
    EventLeakConsumer.cs            ← detects event field delegate list growth by invocation list length; ≤ 100 lines
```

**`DumpCollector.CollectHeapObjectsCombined` (post-redesign)**:
```csharp
// ~20 lines — orchestrates consumers, reads results
var consumers = new IHeapObjectConsumer[]
{
    new TypeStatsConsumer(), new InboundRefConsumer(), new StringGroupConsumer(),
    new GenCounterConsumer(), new ExceptionCountConsumer(), new AsyncMethodConsumer(),
    new TimerConsumer(), new WcfStateConsumer(), new ConnectionConsumer(), new EventLeakConsumer(),
};
HeapWalker.Walk(ctx.Heap, consumers);
// read accumulated results from each consumer and populate DumpSnapshot + HeapSnapshot
```

### 4.5 Project: `DumpDetectiveV2.Reporting`

**Role**: Everything that turns a `ReportDoc` tree or an `IRenderSink` call sequence into output bytes. Zero knowledge of heap walking or command logic.

**NuGet packages**: All arrive transitively from Core.

**File layout**:
```
DumpDetectiveV2.Reporting/
  DumpDetectiveV2.Reporting.csproj   (Ref: Core)
  Sinks/
    HtmlSink.cs          ← self-contained HTML; inline CSS/JS; sticky sidebar nav; collapsible cards; client-side sort+search; virtual scroll >200 rows; print styles; score badges; tooltip lookup by sectionKey first, title-match fallback
    MarkdownSink.cs      ← GFM output; blockquote alerts with emoji; GFM tables; ### details headings
    TextSink.cs          ← ASCII-aligned tables (column widths computed); ═══ headers; --- section dividers
    ConsoleSink.cs       ← Spectre.Console rich tables / panels / Rule separators / hyperlinks
    JsonSink.cs          ← delegates all calls to CaptureSink internally; on Dispose() serializes ReportDoc to DumpReportEnvelope JSON
    CaptureSink.cs       ← in-memory IRenderSink that builds a ReportDoc tree; used for parallel sub-report buffering and by JsonSink
  ReportDocReplay.cs     ← Replay(ReportDoc, IRenderSink): walks Chapter → Section → Element tree; replays every call on the sink in original order
  ToolMemoryDiagnostic.cs
```

**`IRenderSink.Create` factory (in Core, dispatches to Reporting sinks)**:
```csharp
static IRenderSink Create(string? outputPath) => outputPath switch
{
    null           => new ConsoleSink(),
    var p when p.EndsWith(".html", StringComparison.OrdinalIgnoreCase) => new HtmlSink(p),
    var p when p.EndsWith(".md",   StringComparison.OrdinalIgnoreCase) => new MarkdownSink(p),
    var p when p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) => new JsonSink(p),
    var p                                                               => new TextSink(p),
};
```

### 4.6 Project: `DumpDetectiveV2.Commands`

**Role**: All 31 command implementations. Each class implements `ICommand`. No command has any responsibility outside of analysis logic and document production.

**NuGet packages**: None — all arrive transitively through Core + Analysis + Reporting.

**Command implementation pattern** (every command must follow this):
```csharp
public sealed class HeapStatsCommand : ICommand
{
    public string Name => "heap-stats";
    public string Description => "Top types by retained size and instance count.";
    public bool IncludeInFullAnalyze => true;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, HelpText)) return 0;
        var a = CliArgs.Parse(args);
        return CommandBase.Execute(a.DumpPath, a.OutputPath,
            (ctx, sink) => Render(ctx, sink, a));
    }

    public void Render(DumpContext ctx, IRenderSink sink)
        => Render(ctx, sink, CliArgs.Parse([]));

    // Default ICommand.Render implementation produced by BuildReport:
    public ReportDoc BuildReport(DumpContext ctx)
    {
        var capture = new CaptureSink();
        Render(ctx, capture, CliArgs.Parse([]));
        return capture.Doc;
    }

    private static void Render(DumpContext ctx, IRenderSink sink, CliArgs args) { ... }
}
```

**File layout**:
```
DumpDetectiveV2.Commands/
  DumpDetectiveV2.Commands.csproj   (Ref: Core, Analysis, Reporting)
  AnalyzeCommand.cs
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
```

### 4.7 Project: `DumpDetectiveV2.Cli`

**Role**: Entry point only. Owns `CommandRegistry` and the 10-line `Program.cs`. This is the only project with `<OutputType>Exe</OutputType>` and `<PublishAot>true</PublishAot>`.

**Why AOT only here**: Class libraries compile as standard .NET assemblies. The native AOT linker runs once at publish time for the executable project and tree-shakes all transitively referenced code. All six projects publish into one native binary.

**NuGet packages**: None.

**File layout**:
```
DumpDetectiveV2.Cli/
  DumpDetectiveV2.Cli.csproj   ← OutputType=Exe; PublishAot=true; InvariantGlobalization=true; Ref: Commands
  Program.cs                   ← ~10 lines of top-level statements
  CommandRegistry.cs           ← static ICommand[] _all = { new HeapStatsCommand(), ... }; Find / All / FullAnalyzeCommands
  Properties/
    launchSettings.json
    PublishProfiles/
      FolderProfile.pubxml
```

**`Program.cs`**:
```csharp
// top-level statements only
if (args.Length == 0 || args[0] is "--help" or "-h")
{
    PrintHelp();
    return 0;
}

var commandArgs = args[1..];
if (args[0] == "--debug") { ToolMemoryDiagnostic.Enable(); /* shift */ }

var cmd = CommandRegistry.Find(args[0]);
if (cmd is null)
{
    AnsiConsole.MarkupLine($"[red]Unknown command:[/] {Markup.Escape(args[0])}");
    return 1;
}
return cmd.Run(commandArgs);
```

**`CommandRegistry.cs`**:
```csharp
public static class CommandRegistry
{
    private static readonly ICommand[] _all =
    [
        new HeapStatsCommand(),
        new GenSummaryCommand(),
        new HighRefsCommand(),
        // ... all 31
    ];

    public static ICommand? Find(string name)
        => Array.Find(_all, c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<ICommand> All => _all;

    public static IEnumerable<ICommand> FullAnalyzeCommands
        => _all.Where(c => c.IncludeInFullAnalyze);
}
```

### 4.8 Project: `DumpDetectiveV2.Tests`

**Role**: All automated tests. No AOT — this is a normal .NET 10 test project.

**NuGet packages**: `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`.

**File layout**:
```
DumpDetectiveV2.Tests/
  DumpDetectiveV2.Tests.csproj   (Ref: Core, Analysis, Reporting, Commands; no AOT)
  HealthScorer/
    HealthScorerTests.cs         ← 100% branch coverage; no dump file; pure POCO inputs
  Rendering/
    ReportDocReplayTests.cs      ← hand-crafted ReportDoc; assert exact sink call sequence
    HtmlSinkTests.cs             ← verify HTML structure, tooltip key lookup, sectionKey fallback
    MarkdownSinkTests.cs         ← verify GFM table output, alert blockquotes
  CLI/
    ArgumentParserTests.cs       ← --help, -o, DD_DUMP env, unknown flags, missing positional
  Thresholds/
    ThresholdLoaderTests.cs      ← missing file, invalid JSON, partial override, all-defaults
  GoldenFiles/
    heap-stats.json              ← serialized ReportDoc; regenerated by deleting + re-running
    gen-summary.json
    ... (31 total; one per command)
  Fixtures/
    DumpFixture.cs               ← [Collection("Integration")]; skips if DD_TEST_DUMP not set
  Integration/
    AllCommandsRunTest.cs        ← calls cmd.Run([dumpPath]) for every command; asserts exit 0
```

**Golden file test pattern**:
```csharp
[Fact]
public void HeapStats_BuildReport_MatchesGolden()
{
    var snap = GoldenSnapshots.HeapStats;   // pre-built DumpSnapshot fixture
    var ctx  = new FakeDumpContext(snap);
    var doc  = new HeapStatsCommand().BuildReport(ctx);
    var json = JsonSerializer.Serialize(doc, CoreJsonContext.Default.ReportDoc);
    var golden = File.ReadAllText("GoldenFiles/heap-stats.json");
    Assert.Equal(golden.Trim(), json.Trim());
}
```

### 4.9 Package-to-Project Matrix

| NuGet Package | Core | Analysis | Reporting | Commands | Cli | Tests |
|---|:---:|:---:|:---:|:---:|:---:|:---:|
| `Microsoft.Diagnostics.Runtime` 3.1.x | ✓ | transitive | — | — | — | — |
| `Microsoft.Diagnostics.Tracing.TraceEvent` 3.1.x | — | ✓ | — | — | — | — |
| `Spectre.Console` 0.55.x | ✓ | — | transitive | — | — | — |
| `System.Text.Json` (built-in .NET 10) | ✓ | — | transitive | — | — | — |
| `xunit` 2.x | — | — | — | — | — | ✓ |
| `Microsoft.NET.Test.Sdk` | — | — | — | — | — | ✓ |

### 4.10 `.csproj` Templates

**`DumpDetectiveV2.Core.csproj`**:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>DumpDetectiveV2.Core</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Diagnostics.Runtime" Version="3.1.512801" />
    <PackageReference Include="Spectre.Console" Version="0.55.0" />
  </ItemGroup>
</Project>
```

**`DumpDetectiveV2.Analysis.csproj`**:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>DumpDetectiveV2.Analysis</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Diagnostics.Tracing.TraceEvent" Version="3.1.7" />
    <ProjectReference Include="..\DumpDetectiveV2.Core\DumpDetectiveV2.Core.csproj" />
  </ItemGroup>
</Project>
```

**`DumpDetectiveV2.Reporting.csproj`**:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>DumpDetectiveV2.Reporting</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\DumpDetectiveV2.Core\DumpDetectiveV2.Core.csproj" />
  </ItemGroup>
</Project>
```

**`DumpDetectiveV2.Commands.csproj`**:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>DumpDetectiveV2.Commands</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\DumpDetectiveV2.Core\DumpDetectiveV2.Core.csproj" />
    <ProjectReference Include="..\DumpDetectiveV2.Analysis\DumpDetectiveV2.Analysis.csproj" />
    <ProjectReference Include="..\DumpDetectiveV2.Reporting\DumpDetectiveV2.Reporting.csproj" />
  </ItemGroup>
</Project>
```

**`DumpDetectiveV2.Cli.csproj`**:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>DumpDetectiveV2.Cli</RootNamespace>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\DumpDetectiveV2.Commands\DumpDetectiveV2.Commands.csproj" />
  </ItemGroup>
</Project>
```

**`DumpDetectiveV2.Tests.csproj`**:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit" Version="2.9.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <ProjectReference Include="..\DumpDetectiveV2.Core\DumpDetectiveV2.Core.csproj" />
    <ProjectReference Include="..\DumpDetectiveV2.Analysis\DumpDetectiveV2.Analysis.csproj" />
    <ProjectReference Include="..\DumpDetectiveV2.Reporting\DumpDetectiveV2.Reporting.csproj" />
    <ProjectReference Include="..\DumpDetectiveV2.Commands\DumpDetectiveV2.Commands.csproj" />
  </ItemGroup>
</Project>
```

---

## 5. Proposed Architecture — Detail

### 4.1 Command Registry (addresses P1)

#### 4.1.1 Description

Replace the dual-list manual registration with a single source of truth: a `CommandRegistry` that both `Program.cs` and `AnalyzeCommand` consume.

#### 4.1.2 Interface

```csharp
// Core/ICommand.cs
public interface ICommand
{
    string Name { get; }
    bool IncludeInFullAnalyze { get; }
    int Run(string[] args);
    void Render(DumpContext ctx, IRenderSink sink);
}
```

#### 4.1.3 Registry

```csharp
// Core/CommandRegistry.cs
public static class CommandRegistry
{
    private static readonly IReadOnlyDictionary<string, ICommand> _commands;

    static CommandRegistry()
    {
        var commands = new ICommand[]
        {
            new HeapStatsCommand(),
            new GenSummaryCommand(),
            // ... all 31 commands
        };
        _commands = commands.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }

    public static ICommand? Find(string name) => _commands.GetValueOrDefault(name);
    public static IEnumerable<ICommand> FullAnalyzeCommands => _commands.Values.Where(c => c.IncludeInFullAnalyze);
    public static IEnumerable<ICommand> All => _commands.Values;
}
```

#### 4.1.4 Impact on Program.cs

```csharp
// Program.cs (simplified)
var command = CommandRegistry.Find(args[0]);
if (command is null) { PrintHelp(); return 1; }
return command.Run(commandArgs);
```

#### 4.1.5 Impact on AnalyzeCommand

```csharp
// AnalyzeCommand.RenderEmbeddedReports (simplified)
var subCommands = CommandRegistry.FullAnalyzeCommands.ToArray();
var captures = subCommands.Select(_ => new CaptureSink()).ToArray();
Parallel.For(0, subCommands.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
{
    CommandBase.SuppressVerbose = true;
    subCommands[i].Render(ctx, captures[i]);
});
foreach (var (cmd, capture) in subCommands.Zip(captures))
    ReportDocReplay.Replay(capture.Doc, sink);
```

Adding a new command that participates in full-analyze:
1. Create the command class.
2. Add one entry to the `CommandRegistry` array.
3. Set `IncludeInFullAnalyze = true`.

No other files require changes.

---

### 4.2 Document-First Output Pipeline (addresses P2)

#### 4.2.1 Description

Invert the primary output direction. Commands produce a `ReportDoc` tree first; rendering is a second pass through any `IRenderSink`. The streaming sink remains as a convenience wrapper that builds a `ReportDoc` internally.

#### 4.2.2 Primary Command Signature

```csharp
public interface ICommand
{
    // ...
    ReportDoc BuildReport(DumpContext ctx);  // primary path
    void Render(DumpContext ctx, IRenderSink sink);  // default impl: sink.Replay(BuildReport(ctx))
}
```

Default `Render` implementation:

```csharp
public virtual void Render(DumpContext ctx, IRenderSink sink)
    => ReportDocReplay.Replay(BuildReport(ctx), sink);
```

#### 4.2.3 Benefits

| Concern | Current | Redesigned |
|---|---|---|
| Parallel buffering | `CaptureSink` + `ReportDocReplay` shim | Just serialize `ReportDoc` from `BuildReport` |
| JSON archiving | `JsonSink` delegates to `CaptureSink` | Serialize the `ReportDoc` directly |
| Trend replay | Load JSON → `ReportDocReplay` → sink | Same |
| New IRenderSink method | 4 files updated | 2 files updated (`IRenderSink` + implementation) |

#### 4.2.4 Migration Note

`CaptureSink` and `ReportDocReplay` remain in the codebase — they are correct and useful. The change is that `BuildReport` becomes the canonical command output path, with `Render` as a convenience shim.

---

### 4.3 HealthScorer Class (addresses P3)

#### 4.3.1 Description

Extract `GenerateFindings` from `DumpCollector` into a dedicated `HealthScorer` class.

#### 4.3.2 Interface

```csharp
// Core/HealthScorer.cs
public static class HealthScorer
{
    public static (IReadOnlyList<Finding> Findings, int Score) Score(
        DumpSnapshot snap,
        ScoringThresholds thresholds);
}
```

#### 4.3.3 DumpCollector After Extraction

`DumpCollector` retains only:
- `CollectThreads`, `CollectThreadPool`, `CollectHandles`, `CollectModules`, `CollectSegmentLayout`
- `CollectHeapObjects` / `CollectHeapObjectsCombined`
- `CollectFinalizerQueue`

`DumpCollector` no longer has any dependency on `ThresholdLoader`.

#### 4.3.4 Testability

`HealthScorer.Score` takes two plain POCOs — no ClrMD types, no file I/O. It is fully unit testable with no dump file.

---

### 4.4 Heap Walk Visitor Pipeline (addresses P4)

#### 4.4.1 Description

Replace the monolithic `CollectHeapObjectsCombined` accumulator with a visitor pipeline.

#### 4.4.2 Interface

```csharp
// Collectors/IHeapObjectConsumer.cs
public interface IHeapObjectConsumer
{
    void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap);
    void Finalize();  // called once after walk completes
}
```

#### 4.4.3 Walk Engine

```csharp
// Collectors/HeapWalker.cs
public static class HeapWalker
{
    public static void Walk(ClrHeap heap, IReadOnlyList<IHeapObjectConsumer> consumers)
    {
        var typeMetaCache = new Dictionary<ulong, HeapTypeMeta>();
        foreach (var obj in heap.EnumerateObjects())
        {
            if (obj.IsNull) continue;
            var meta = GetOrBuildMeta(typeMetaCache, obj, heap);
            foreach (var c in consumers)
                c.Consume(in obj, meta, heap);
        }
        foreach (var c in consumers)
            c.Finalize();
    }
}
```

#### 4.4.4 Consumers

Each consumer is a small, independently testable class:

```
Collectors/Consumers/
  TypeStatsConsumer.cs      → builds TypeAgg dict for HeapSnapshot
  InboundRefConsumer.cs     → builds InboundCounts dict for HeapSnapshot
  StringGroupConsumer.cs    → builds StringGroups dict for HeapSnapshot
  GenCounterConsumer.cs     → accumulates gen0/1/2/loh/poh totals
  ExceptionCountConsumer.cs → counts live exception instances per type
  AsyncMethodConsumer.cs    → counts active async state machines
  TimerConsumer.cs          → counts live Timer objects
  WcfStateConsumer.cs       → reads WCF channel state fields
  ConnectionConsumer.cs     → counts DB connections by state
  EventLeakConsumer.cs      → detects event field delegate list growth
```

#### 4.4.5 Composing a Full Walk

```csharp
// DumpCollector.CollectHeapObjectsCombined (simplified)
var consumers = new IHeapObjectConsumer[]
{
    new TypeStatsConsumer(),
    new InboundRefConsumer(),
    new StringGroupConsumer(),
    new GenCounterConsumer(),
    new ExceptionCountConsumer(),
    new AsyncMethodConsumer(),
    new TimerConsumer(),
    new WcfStateConsumer(),
    new ConnectionConsumer(),
    new EventLeakConsumer(),
};
HeapWalker.Walk(ctx.Heap, consumers);
// read results from each consumer
```

Adding a new heap-level metric: add a new `IHeapObjectConsumer` implementation and register it. The hot loop and all other consumers are untouched.

---

### 4.5 Single Snapshot Model (addresses P5)

#### 4.5.1 Description

Design `DumpSnapshot` with AOT-safe types from the start, eliminating `SnapshotData` as a separate class.

#### 4.5.2 Rules for DumpSnapshot Fields

- No `ValueTuple` fields.
- No anonymous types.
- All collection elements: named `record` types.
- All `record` types used as collection elements: registered with `[JsonSerializable]` in `DumpSnapshotContext`.

#### 4.5.3 Eliminated Code

- `SnapshotData.cs` (the entire file)
- `SnapshotData.From(DumpSnapshot)` conversion
- `SnapshotData.ToSnapshot()` conversion
- `DumpReportEnvelope` moves to `DumpSnapshot.cs` (it is only a JSON wrapper)

#### 4.5.4 Migration Note

`SnapshotData.SubReport` (which holds a captured `ReportDoc` for a sub-command) moves to a `DumpSnapshot.SubReport` property. Its semantics are unchanged.

---

### 4.6 Test Project (addresses P6)

#### 4.6.1 Test Project Setup

```
DumpDetective.Tests/
  DumpDetective.Tests.csproj   (xUnit, net10.0, no AOT)
  HealthScorer/
    HealthScorerTests.cs
  Rendering/
    HtmlSinkTests.cs
    MarkdownSinkTests.cs
    ReportDocReplayTests.cs
  CLI/
    ArgumentParserTests.cs
  Thresholds/
    ThresholdLoaderTests.cs
  GoldenFiles/
    heap-stats.json
    gen-summary.json
    (one golden ReportDoc per command)
  Fixtures/
    DumpFixture.cs   ← conditionally skips tests if no .dmp file available
```

#### 4.6.2 Test Categories

**Unit tests (no dump file required)**:
- `HealthScorer.Score` with crafted `DumpSnapshot` inputs
- `ThresholdLoader` fallback behavior
- `DumpHelpers.FormatSize`, `IsSystemType`
- `ArgumentParser` parsing edge cases
- `ReportDocReplay` against a hand-crafted `ReportDoc`

**Rendering snapshot tests (no dump file required)**:
- Each command's `BuildReport` method is called with a faked `DumpContext` (or a pre-built `DumpSnapshot`) and the resulting `ReportDoc` is compared to a golden JSON file.
- Golden files are regenerated by running `dotnet test --update-golden` (or by deleting the golden file and re-running).

**Integration tests (require a reference .dmp file)**:
- Checked-in small reference dump (< 5 MB, minimal CLR heap).
- Runs each command's full `Run` path and asserts exit code 0.
- Skipped automatically if the reference dump path is not set in an environment variable.

#### 4.6.3 Coverage Goals

| Component | Target |
|---|---|
| `HealthScorer` | 100% branch coverage |
| `ThresholdLoader` | 100% branch coverage |
| `ArgumentParser` | 100% branch coverage |
| `ReportDocReplay` | 100% element type coverage |
| `DumpHelpers` | 100% |
| Rendering pipeline | Golden file match for all 31 commands |
| Integration (with dump) | Exit code 0 for all 31 commands |

---

### 4.7 Shared Argument Parser (addresses P7)

#### 4.7.1 Description

A small shared `CliArgs` helper (< 100 lines) that normalizes argument parsing across all commands.

#### 4.7.2 Interface

```csharp
// Core/CliArgs.cs
public sealed class CliArgs
{
    public string? DumpPath { get; }
    public string? OutputPath { get; }
    public bool Help { get; }

    public bool HasFlag(string name);           // e.g. HasFlag("--blocked-only")
    public string? GetOption(string name);      // e.g. GetOption("--top") → "50"
    public int GetInt(string name, int @default);
    public string GetString(string name, string @default);

    public static CliArgs Parse(string[] args);
}
```

#### 4.7.3 Conventions Enforced

- `--help` / `-h`: always recognized; `TryHelp` integrated.
- `--output` / `-o`: canonical output path option.
- First non-flag positional argument: dump path (unless `DD_DUMP` env var is set).
- Unknown flags: silently ignored (not errors), consistent with current behavior.

---

### 4.8 Structured Tooltip Metadata (addresses P8)

#### 4.8.1 Description

Replace string-matching tooltip lookup in `HtmlSink` with structured section metadata.

#### 4.8.2 Interface Change to IRenderSink

```csharp
// Current
void Section(string title);

// Proposed
void Section(string title, string? sectionKey = null);
```

`sectionKey` is an optional stable identifier (e.g., `"heap-fragmentation"`, `"thread-pool"`). `HtmlSink` indexes tooltips by `sectionKey` if provided, falling back to the current title-matching logic for backward compatibility. All other `IRenderSink` implementations ignore `sectionKey`.

#### 4.8.3 Migration

No command changes required initially — the parameter is optional. Section keys are added incrementally per command when desired.

---

## 6. Functional Requirements

### FR-01 — Command Registration
- The system **shall** require adding a new command to exactly **one** place in the codebase.
- The system **shall** emit a compile-time error (or a startup runtime assertion) if a command registered for full-analyze mode does not implement `ICommand.Render`.

### FR-02 — Output Formats
- The system **shall** continue to support HTML, Markdown, JSON, plain text, and console output formats.
- The system **shall** produce byte-for-byte equivalent output to the current implementation for all existing commands and formats (verified by golden file comparison).

### FR-03 — Document Model as Primary Output
- Every command **shall** provide a `BuildReport(DumpContext) → ReportDoc` implementation.
- `Render(DumpContext, IRenderSink)` **shall** have a default implementation that calls `BuildReport` then `ReportDocReplay.Replay`.
- Commands **may** override `Render` for performance-sensitive cases (e.g., streaming a very large table without materializing the full `ReportDoc`).

### FR-04 — Health Scoring
- `HealthScorer.Score` **shall** accept `(DumpSnapshot, ScoringThresholds)` and return `(IReadOnlyList<Finding>, int score)`.
- `HealthScorer` **shall** have no dependency on `ClrMD` types.
- `HealthScorer` **shall** produce identical results to the current `DumpCollector.GenerateFindings` for all input combinations.

### FR-05 — Heap Walk Visitors
- `HeapWalker.Walk` **shall** iterate `heap.EnumerateObjects()` exactly once regardless of how many consumers are registered.
- Each `IHeapObjectConsumer` **shall** be independently constructable and testable without a heap walk (i.e., `Consume` and `Finalize` are the only external dependencies).
- `HeapWalker` **shall** call `Finalize()` on each consumer exactly once, after the walk completes, even if the walk throws.

### FR-06 — Single Snapshot Model
- `DumpSnapshot` **shall** contain no `ValueTuple` fields.
- `DumpSnapshot` **shall** be directly JSON serializable via an AOT source-gen context without a conversion step.
- `SnapshotData` **shall** be removed from the codebase.

### FR-07 — Argument Parsing
- All commands **shall** use `CliArgs.Parse` for argument parsing.
- `--help` / `-h` **shall** be recognized by all commands.
- `--output` / `-o` **shall** be recognized by all commands.

### FR-08 — Testing
- The solution **shall** include a `DumpDetectiveV2.Tests` project with the structure defined in §4.8.
- `HealthScorer`, `ThresholdLoader`, `DumpHelpers`, `CliArgs`, and `ReportDocReplay` **shall** have unit tests that pass without a dump file.
- All 31 commands **shall** have golden file rendering tests that pass without a dump file.

### FR-09 — Backward Compatibility
- All existing CLI invocations (command names, flags, environment variables) **shall** continue to work without modification.
- The `dd-thresholds.json` format **shall** be unchanged.
- The `--output` file extension → format mapping **shall** be unchanged.

---

## 7. Non-Functional Requirements

### NFR-01 — Performance
- The redesigned heap walk pipeline **shall** not be measurably slower than the current `CollectHeapObjectsCombined` implementation on the same input dump. (Target: < 5% regression on wall-clock time for full-analyze mode.)
- `IHeapObjectConsumer.Consume` **shall** be called in a tight loop; implementations **shall** not allocate on the hot path.

### NFR-02 — Native AOT Compatibility
- All new code **shall** be compatible with `PublishAot=true`.
- No new `System.Reflection` or `System.Linq.Expressions` usage.
- Any new JSON serialization **shall** use source-gen contexts.

### NFR-03 — Build
- `dotnet build DumpDetectiveV2.slnx -c Debug` **shall** produce zero errors and zero new warnings after each change.
- `dotnet test DumpDetectiveV2.Tests` **shall** pass with zero failures.

### NFR-04 — Code Size
- `CliArgs` **shall** be ≤ 100 lines.
- `HealthScorer` **shall** be ≤ 200 lines.
- `HeapWalker` **shall** be ≤ 60 lines (all complexity lives in consumers).
- Each `IHeapObjectConsumer` implementation **shall** be ≤ 100 lines.

### NFR-05 — Discoverability
- A developer adding a new command **shall** need to read only `ICommand.cs`, `CommandRegistry.cs`, and one existing command as a reference.

---

## 8. Components to Preserve Unchanged

The following components are well-designed and correct. The redesign **shall not** modify them except where required by interface changes in §5.

| Component | Reason to Keep |
|---|---|
| `IRenderSink` interface | Clean abstraction; adding `sectionKey` is additive |
| `HtmlSink` HTML/CSS/JS | Works correctly; tooltip fix is a minor additive change |
| `MarkdownSink`, `TextSink`, `ConsoleSink` | No issues identified |
| `CaptureSink` + `ReportDocReplay` | Correct; become less critical but remain useful |
| `HeapSnapshot` | Well-designed cache; populated by new visitor consumers |
| `DumpContext` | `EnsureSnapshot` + `PreloadSnapshot` pattern is correct |
| `ThresholdLoader` | Silent fallback, AOT source-gen, lazy singleton — all correct |
| `DumpHelpers` | Small, correct, no issues |
| `Finding` record | Correct immutable model |
| `ReportDoc` model | Polymorphic JSON, AOT-safe — correct |
| `ThresholdConfig` POCOs | Correct |
| `dd-thresholds.json` format | No change needed |
| Single combined heap walk | The optimization is kept; `HeapWalker` is a refactor, not a replacement |
| `[ThreadStatic] SuppressVerbose` | Correct parallel isolation technique |
| `CollectionsMarshal.GetValueRefOrAddDefault` | Kept in all hot loops |

---

## 9. Migration Path

The following sequence avoids a big-bang rewrite. Each step is independently buildable and testable.

### Step 1 — Add CliArgs (P7, low risk)
- Implement `CliArgs.Parse` in `Core/CliArgs.cs`.
- Migrate commands one-by-one, starting with the simplest (e.g., `ModuleListCommand`).
- No behavioral changes — parsing is equivalent.

### Step 2 — Extract HealthScorer (P3, low risk)
- Move `GenerateFindings` body to `HealthScorer.Score(DumpSnapshot, ScoringThresholds)`.
- `DumpCollector.GenerateFindings` becomes a one-line call: `return HealthScorer.Score(snap, ThresholdLoader.Current.Scoring);`
- Add unit tests for `HealthScorer`.

### Step 3 — Unify DumpSnapshot/SnapshotData (P5, medium risk)
- Replace `ValueTuple` fields in `DumpSnapshot` with named records.
- Register new types in AOT source-gen context.
- Delete `SnapshotData.cs`.
- Update all call sites (mostly `AnalyzeCommand`, `TrendAnalysisCommand`, `TrendRawSerializer`).
- Verify JSON output format compatibility with existing saved snapshots (add a read-back test).

### Step 4 — Add Test Project (P6, ongoing)
- Create `DumpDetective.Tests.csproj`.
- Add unit tests for `CliArgs` (Step 1), `HealthScorer` (Step 2), `ThresholdLoader`, `DumpHelpers`.
- Add golden file rendering tests (empty golden files initially; run once to generate baselines).

### Step 5 — Command Registry (P1, medium risk)
- Introduce `ICommand` interface.
- Convert commands to implement `ICommand` (can be done incrementally; `Program.cs` switch stays until all commands are converted).
- Switch `Program.cs` to `CommandRegistry.Find`.
- Switch `AnalyzeCommand.RenderEmbeddedReports` to `CommandRegistry.FullAnalyzeCommands`.
- Delete the switch expression and the 23-item array.

### Step 6 — Heap Walk Visitors (P4, medium risk)
- Implement `IHeapObjectConsumer` and `HeapWalker`.
- Extract one consumer at a time from `CollectHeapObjectsCombined` (start with `TypeStatsConsumer`).
- Run full-analyze mode after each extraction; compare output to golden files.
- After all consumers are extracted, `CollectHeapObjectsCombined` becomes a ~20-line orchestrator.

### Step 7 — Document-First Pipeline (P2, high risk, last)
- Add `BuildReport(DumpContext) → ReportDoc` to `ICommand`.
- Add default `Render` implementation via `ReportDocReplay`.
- Migrate commands one-by-one to implement `BuildReport` (can be done incrementally alongside `Render`).
- After all 31 commands are migrated, `CaptureSink` usage in `AnalyzeCommand` simplifies to collecting `ReportDoc` objects directly.

### Step 8 — HtmlSink Tooltip Keys (P8, low risk)
- Add `sectionKey` parameter to `IRenderSink.Section` (optional, default null).
- Update `HtmlSink` to use key-based lookup with title-matching fallback.
- Add section keys to commands incrementally.

---

## 10. Glossary

| Term | Definition |
|---|---|
| ClrMD | Microsoft.Diagnostics.Runtime — the library used to open and inspect .NET memory dumps |
| `DumpContext` | A wrapper around one open ClrMD `DataTarget` + `ClrRuntime`; the primary dependency injected into all commands |
| `HeapSnapshot` | In-memory cache of one full `EnumerateObjects` pass: type statistics, inbound reference counts, string groups, generation counters |
| `DumpSnapshot` | All collected data for one dump: memory layout, thread state, handles, exceptions, findings, health score |
| `ReportDoc` | A serializable document tree (Chapter → Section → Element) representing one command's output |
| `IRenderSink` | The output abstraction: 12 semantic methods (Header, Section, Table, Alert, etc.) that all output formats implement |
| `CaptureSink` | An `IRenderSink` that builds a `ReportDoc` in memory instead of writing to a file/console |
| `ReportDocReplay` | A utility that walks a `ReportDoc` tree and replays every call against any `IRenderSink` |
| `Finding` | One health issue identified during analysis: severity, category, headline, detail, advice, score deduction |
| Full-analyze mode | The `analyze --full` path that runs all 23 sub-commands in parallel and combines output into one report |
| AOT | Ahead-Of-Time native compilation (`PublishAot=true`); prohibits runtime reflection for JSON serialization |
| Golden file | A checked-in reference output (JSON serialized `ReportDoc`) used as the expected value in rendering snapshot tests |
| `IHeapObjectConsumer` | Proposed interface for a single-responsibility heap walk accumulator |
| Consumer pipeline | The proposed replacement for `CollectHeapObjectsCombined`: multiple `IHeapObjectConsumer` instances registered with `HeapWalker` |
