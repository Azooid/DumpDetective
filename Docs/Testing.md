# DumpDetective — Test Architecture

## Overview

The test suite is in `DumpDetective.Tests/`. It covers all 24 analysis commands (72 tests total) through scenario-based integration tests. Every test runs against a real heap dump — no mocks, no fake ClrMD data.

```
72 tests = 24 commands × 3 facts each
  Report_Summary      — always passes; prints section/table/alert counts to output
  Report_HasContent   — doc has at least one non-empty chapter
  Scenario_Validates  — scenario-specific structural + data assertions
```

---

## Running Tests

```bash
# Run all tests
dotnet test DumpDetective.Tests

# Run with verbose output (shows Report_Summary output for all 24 commands)
dotnet test DumpDetective.Tests --logger "console;verbosity=detailed"

# Force regeneration of all scenario dumps (e.g. after changing ScenarioHost)
$env:DD_REFRESH_DUMPS = "1"; dotnet test DumpDetective.Tests

# Run a single command's tests
dotnet test DumpDetective.Tests --filter "FullyQualifiedName~HeapStats"
```

**First run**: ~30 seconds — generates 24 isolated heap dumps via `DumpDetective.ScenarioHost`.  
**Subsequent runs**: ~700 ms — reuses cached dumps from `%TEMP%\DumpDetective\Scenarios\`.

---

## Dump Lifecycle

### Per-scenario dump cache

Each command gets its own isolated dump at:

```
%TEMP%\DumpDetective\Scenarios\<command-name>.dmp
```

For example: `heap-stats.dmp`, `finalizer-queue.dmp`, `thread-analysis.dmp`, etc.

Dump resolution order in `CommandContext<TScenario>`:

1. **Cached dump** — `%TEMP%\DumpDetective\Scenarios\<command-name>.dmp` exists **and** is newer than the `DumpDetective.ScenarioHost.exe` binary → reuse immediately.
2. **ScenarioHost generation** — runs `DumpDetective.ScenarioHost.exe <command-name>` as a subprocess. The host sets up the scenario state, captures a heap dump of itself, writes it to the per-scenario path, and exits. Stdout contains the dump path.
3. **Shared dump fallback** — if ScenarioHost is unavailable, falls back to the single combined dump captured by `ScenarioFixture` at test startup (all scenarios set up in the test-runner process, one shared dump).

### Automatic freshness

`CommandContext` compares the `ScenarioHost.exe` write time against the cached dump write time. If the binary is newer the dump is regenerated automatically — no manual `DD_REFRESH_DUMPS=1` needed after a `dotnet build`.

### Forcing regeneration

```bash
# Regenerate all scenario dumps
$env:DD_REFRESH_DUMPS = "1"; dotnet test DumpDetective.Tests

# Delete specific scenario dump to force one regeneration
Remove-Item "$env:TEMP\DumpDetective\Scenarios\heap-stats.dmp"
dotnet test DumpDetective.Tests --filter "FullyQualifiedName~HeapStats"
```

### HTML reports

After each `BuildReport` call, the fixture writes an HTML file alongside the dump:

```
%TEMP%\DumpDetective\Scenarios\Reports\<command-name>_<command-name>.html
```

These are written unconditionally on every test run and survive test cleanup.

---

## Project Layout

```
DumpDetective.Tests/
  DumpDetective.Tests.csproj      net10.0; xUnit 2.9.3; refs Core + Analysis + Reporting + Commands + Cli

  Fixtures/
    IScenario.cs                  Interface: CommandName, Description, SafeInProcess, Setup, Validate, Teardown
    ScenarioFixture.cs            ICollectionFixture — shared dump fallback; owns process-level lifecycle
    CommandContext.cs             IClassFixture<T> — resolves dump, calls BuildReport, writes HTML
    SelfDumpCapture.cs            DiagnosticsClient.WriteDump wrapper
    DocAssert.cs                  ReportDoc assertion helpers (HasSection, AnyTableContainsText, etc.)

  Scenarios/
    AllScenarios.cs               Static lists: AllScenarios.Safe + AllScenarios.All
    Async/
      AsyncStacksScenario.cs      101 suspended state machines
      HttpRequestsScenario.cs     15 HttpClient + 20 HttpRequestMessage (dummyjson.com)
    Exceptions/
      EventAnalysisScenario.cs    200 lambda subscribers on a static event
      ExceptionAnalysisScenario.cs 5 exception types × 30 instances each
    Gc/
      FinalizerQueueScenario.cs   200 DdFinalizableItem behind a blocked finalizer thread
      HandleTableScenario.cs      30 Normal + 30 Weak + 30 WeakTrackResurrection handles
      PinnedObjectsScenario.cs    100 GCHandle.Pinned byte arrays
      StaticRefsScenario.cs       DdStaticAnchors.Root → DdStaticHolder (100 nodes)
      WeakRefsScenario.cs         1001 WeakReference<WeakTarget> with live targets
    Heap/
      GenSummaryScenario.cs       2 000 Gen2-promoted objects + LOH arrays
      HeapFragmentationScenario.cs 25 pinned + 25 freed LOH arrays
      HeapStatsScenario.cs        500 instances × 5 named types (HsType*)
      HighRefsScenario.cs         Hub object with 2 000 inbound references
      LargeObjectsScenario.cs     20 × 200 KB byte arrays on the LOH
      MemoryLeakScenario.cs       50 × 100 KB byte arrays in a static list
      StringDuplicatesScenario.cs 4 templates × 200 heap copies each
    Leaks/
      ConnectionPoolScenario.cs   No-setup (no SqlClient stubs) — structural test
      TimerLeaksScenario.cs       200 System.Threading.Timer never disposed
      WcfChannelsScenario.cs      No-setup (no ServiceModel stubs) — structural test
    Thread/
      DeadlockScenario.cs         SafeInProcess=false — clean-process dump
      ThreadAnalysisScenario.cs   20 named threads (DDTestWorker-XX) blocked on a gate
      ThreadPoolScenario.cs       SafeInProcess=false — clean-process dump
    Types/
      ModuleListScenario.cs       No-setup — every .NET process has modules
      TypeInstancesScenario.cs    500 TargetObject instances; tests missing-arg alert

  Integration/
    ScenarioTests.cs              ScenarioTestBase<T>: base class with [Collection("Scenarios")],
                                  Report_Summary, Report_HasContent facts
    HeapStatsTest.cs              }
    GenSummaryTest.cs             } One file per command — IClassFixture<CommandContext<TScenario>>
    ...                           } + Scenario_Validates fact
    WeakRefsTest.cs               }

DumpDetective.ScenarioHost/
  Program.cs                      Entry: runs scenario by name, dumps self, prints path, exits 0
  Scenarios.cs                    24 IScenario implementations (same type names as test assertions)
```

---

## How a Test Runs (End to End)

```
xUnit discovers HeapStatsTest
  └─ IClassFixture<CommandContext<HeapStatsScenario>> constructed
       └─ CommandContext.ResolveScenarioDump("heap-stats")
            ├─ [1] %TEMP%\DumpDetective\Scenarios\heap-stats.dmp exists && fresh? → return path
            ├─ [2] FindScenarioHostExe() found → spawn ScenarioHost.exe heap-stats
            │         ScenarioHost: HeapStatsScenario.Setup()       → allocate 2 500 objects
            │         ScenarioHost: await Task.Delay(300)           → let state settle
            │         ScenarioHost: DiagnosticsClient.WriteDump()   → heap-stats.dmp
            │         ScenarioHost: HeapStatsScenario.Teardown()
            │         ScenarioHost: Console.WriteLine(dumpPath)     → read by test process
            │         ScenarioHost: exit 0
            │         Test process reads stdout → returns dump path
            └─ [3] fallback: ScenarioFixture.SharedDumpPath
       └─ DumpContext.Open(dumpPath)
       └─ HeapStatsCommand.BuildReport(ctx)            → ReportDoc
       └─ HtmlSink → %TEMP%\...\Reports\heap-stats_heap-stats.html
  └─ HeapStatsTest.Report_Summary    → prints sections/tables/alerts
  └─ HeapStatsTest.Report_HasContent → DocAssert.HasContent(Doc)
  └─ HeapStatsTest.Scenario_Validates → HeapStatsScenario.Validate(Doc)
```

---

## `IScenario` Interface

Every scenario implements:

```csharp
public interface IScenario
{
    string CommandName  { get; }        // matches ICommand.Name, e.g. "heap-stats"
    string Description  { get; }        // shown in Report_Summary output
    bool   SafeInProcess => true;       // false = Setup() skipped (would break test runner)
    void   Setup();                     // allocate objects / start threads / etc.
    void   Validate(ReportDoc doc);     // assert expected content in the report
    void   Teardown() { }               // release gates, dispose handles, etc.
}
```

`SafeInProcess = false` scenarios (`deadlock-detection`, `thread-pool`) have empty `Setup()` implementations. Their dumps capture a clean idle process, and `Validate()` asserts only structural properties.

---

## `DocAssert` Helpers

`DocAssert` is a static helper class for asserting `ReportDoc` structure without hard-coding element indices or section ordering.

| Method | What it checks |
|---|---|
| `HasContent(doc)` | At least one non-empty chapter |
| `HasSection(doc, partialTitle)` | At least one section title contains the substring |
| `HasKeyValue(doc, key)` | At least one `ReportKeyValues` element contains the key |
| `GetKeyValue(doc, key)` | Returns the value string for the key, or `null` |
| `AlertContains(doc, text)` | At least one alert body contains the text |
| `AllAlerts(doc)` | All `ReportAlert` elements (flattened) |
| `AllTables(doc)` | All `ReportTable` elements (flattened, including inside `ReportDetails`) |
| `AllSections(doc)` | All `ReportSection` elements (flattened) |
| `TableHasMinRows(doc, n, reason)` | Any table has ≥ n rows |
| `TableByHeadersHasMinRows(doc, n, reason, headers…)` | Table whose headers contain all required strings has ≥ n rows |
| `AnyTableContainsText(doc, text, reason)` | Any cell in any table contains the text (case-insensitive) |
| `AnyDetailsTitleContains(doc, text, reason)` | Any `ReportDetails` title contains the text |
| `FindTable(doc, predicate, reason)` | Returns the first table satisfying the row predicate; fails if none |

---

## `ScenarioHost` — Isolated Dump Generation

`DumpDetective.ScenarioHost` is a standalone .NET 10 console app. It has no reference to the test project — it only imports `Microsoft.Diagnostics.NETCore.Client`.

**Why a separate process?**

Running all 24 scenarios in the xUnit test-runner process (the old `ScenarioFixture` approach) caused:
- **Cross-contamination** — types from 24 scenarios all appear in every command's report; assertions need very loose lower bounds.
- **ClrMD static-field limitation** — `GetTypeByMethodTable` fails for test-assembly types loaded by the xUnit runner; `static-refs` reported zero results.
- **Name collisions** — type names in the report include the full namespace; test types from 24 different namespaces clutter heap-stats output.

**How it works:**

```
DumpDetective.ScenarioHost.exe <command-name>
```

1. Looks up `<command-name>` in a static `ScenarioRegistry` dictionary.
2. Calls `scenario.Setup()` — allocates exactly the objects that command needs to find.
3. Waits 300 ms for async tasks / threads to reach their blocked state.
4. Calls `DiagnosticsClient.WriteDump(DumpType.WithHeap, path)` on the current process.
5. Writes the dump path to stdout.
6. Calls `scenario.Teardown()`.
7. Exits with code 0.

The test process spawns this as a child process, reads stdout for the dump path, and returns it to `CommandContext`.

**Type naming:**  
All scenario types in `ScenarioHost/Scenarios.cs` are `public` and live in the `DumpDetective.ScenarioHost` namespace. ClrMD reports them as `DumpDetective.ScenarioHost.DdFinalizableItem` etc. Test assertions use substring matching (`AnyTableContainsText(doc, "DdFinalizableItem")`) so the namespace prefix is irrelevant.

---

## Adding a New Test

### 1. Add a scenario class

Create `DumpDetective.Tests/Scenarios/<Category>/<CommandName>Scenario.cs`:

```csharp
public sealed class MyNewCommandScenario : IScenario
{
    private static readonly List<MyObject> _objects = [];

    public string CommandName => "my-new-command";
    public string Description => "100 MyObject instances for my-new-command.";

    public void Setup()
    {
        for (int i = 0; i < 100; i++)
            _objects.Add(new MyObject(i));
    }

    public void Validate(ReportDoc doc)
    {
        DocAssert.HasSection(doc, "My Section");
        DocAssert.AnyTableContainsText(doc, "MyObject");
        DocAssert.AlertContains(doc, "my alert text");
    }

    public void Teardown() => _objects.Clear();
}
```

### 2. Add a ScenarioHost implementation

Add a scenario entry in `DumpDetective.ScenarioHost/Scenarios.cs`:

```csharp
// ── my-new-command ────────────────────────────────────────────────────────────
// The type names here must match the substrings used in Validate() assertions.
public sealed class MyObject(int Id) { public int Id = Id; }

internal sealed class MyNewCommandScenario : IScenario
{
    private static readonly List<MyObject> _objects = [];
    public string CommandName => "my-new-command";

    public void Setup()
    {
        for (int i = 0; i < 100; i++)
            _objects.Add(new MyObject(i));
    }
}
```

Also add it to the `Scenarios.All` array in `Scenarios.cs`.

### 3. Register in `AllScenarios`

Add the scenario to `DumpDetective.Tests/Scenarios/AllScenarios.cs`:

```csharp
public static readonly IReadOnlyList<IScenario> Safe = [
    // ...existing...
    new MyNewCommandScenario(),
];
```

### 4. Add a test class

Create `DumpDetective.Tests/Integration/MyNewCommandTest.cs`:

```csharp
using DumpDetective.Tests.Fixtures;
using DumpDetective.Tests.Scenarios.<Category>;

namespace DumpDetective.Tests.Integration;

public sealed class MyNewCommandTest(CommandContext<MyNewCommandScenario> ctx)
    : ScenarioTestBase<MyNewCommandScenario>(ctx),
      IClassFixture<CommandContext<MyNewCommandScenario>>
{
    [Fact] public void Report_HasContent()  => DocAssert.HasContent(Doc);
    [Fact] public void Scenario_Validates() => Scenario.Validate(Doc);
}
```

### 5. Build and run

```bash
dotnet build DumpDetective.Tests
dotnet test DumpDetective.Tests --filter "FullyQualifiedName~MyNewCommand"
```

The ScenarioHost exe is rebuilt automatically (it's a build dependency of the test project) and the freshness check will generate a new `my-new-command.dmp` on the first run.

---

## Scenario Coverage Table

| Command | Scenario | Key Assertions |
|---|---|---|
| `async-stacks` | 101 `SuspendedWorker` state machines on a TCS | Suspended ≥ 101, `SuspendedWorker` in table, alert fires |
| `connection-pool` | No-setup | HasContent |
| `deadlock-detection` | SafeInProcess=false | Analysis Summary section; no Deadlock Cycles section |
| `event-analysis` | 200 lambda subscribers on `DataReceived` | `DataReceived` in table, subscriber count ≥ 50 |
| `exception-analysis` | 5 types × 30 instances | All 5 type names in table, count ≥ 30 |
| `finalizer-queue` | 200 `DdFinalizableItem` behind blocked finalizer | `DdFinalizableItem` in table, Total in queue ≥ 200 |
| `gen-summary` | Gen2-promoted + LOH arrays | Gen0/Gen1/Gen2/LOH KVs present |
| `handle-table` | 30 Normal + 30 Weak + 30 WeakTrackResurrection | `DdHandleTarget`, Strong, Weak in tables |
| `heap-fragmentation` | 25 pinned + 25 freed LOH arrays | Segment table present, Pinned > 0 |
| `heap-stats` | 500 × 5 types (`HsType*`) | `HsType` in table, ≥ 10 rows |
| `high-refs` | Hub with 2 000 inbound refs | Inbound ref count ≥ 100 |
| `http-requests` | 15 `HttpClient` + 20 `HttpRequestMessage` (dummyjson.com) | All three type names in tables |
| `large-objects` | 20 × 200 KB arrays on LOH | `Byte[]` in table, 20-row individual table |
| `memory-leak` | 50 × 100 KB arrays in static list | `Byte[]`, Suspect Types section |
| `module-list` | No-setup | `System.Private.CoreLib` in table, ≥ 5 rows |
| `pinned-objects` | 100 `GCHandle.Pinned` byte arrays | GC-Pinned ≥ 100 |
| `static-refs` | `DdStaticAnchors.Root` → `DdStaticHolder` (100 nodes) | Section present; `DdStaticHolder` when data found |
| `string-duplicates` | 4 templates × 200 copies | `contoso` in table, Count ≥ 200 |
| `thread-analysis` | 20 threads named `DDTestWorker-XX` | Named threads ≥ 20, `DDTestWorker` in accordion titles |
| `thread-pool` | SafeInProcess=false | Thread Pool State section; HasContent |
| `timer-leaks` | 200 `System.Threading.Timer` | Count ≥ 200 in callback table, alert fires |
| `type-instances` | 500 `TargetObject` instances | "requires --type" alert |
| `wcf-channels` | No-setup | Summary section; HasContent |
| `weak-refs` | 1 001 `WeakReference<WeakTarget>` with live targets | `WeakTarget` in table, alert fires (> 1 000 threshold) |

---

## Known Limitations

### `static-refs` — zero tables in ScenarioHost dumps

**Status:** Accepted limitation.

**Root cause:** ClrMD 3.1.x / .NET 10 — `StaticField.ReadObject(appDomain)` returns invalid objects for static fields in simple console process dumps. The `DeclType` is resolved via `GetTypeByMethodTable`, but `ReadObject` consistently returns `IsValid=false` for application-type statics in lightweight processes.

**Behaviour:** The `static-refs` command still works correctly on real production dumps (IIS, ASP.NET, service processes) where framework singletons populate enough method-table cache entries.

**Test handling:** `StaticRefsScenario.Validate` checks `if (hasData)` before asserting `DdStaticHolder`. When no tables are produced, only the section-presence assertion fires — the test always passes.

### `heap-fragmentation` — 0% fragmentation alert not asserted

**Status:** Accepted limitation.

**Root cause:** .NET 10 region-based GC decommits empty regions entirely rather than leaving placeholder Free objects. ClrMD reports 0 free bytes even when pinned arrays were interleaved with freed ones. The segment table still shows Pinned > 0, which the test asserts.

**Behaviour:** The fragmentation-% alert fires correctly on real pre-.NET 10 GC dumps.

### `connection-pool` / `wcf-channels` — structural only

**Status:** By design.

`ConnectionPool` requires live `SqlConnection` objects with internal state fields. `WcfChannels` requires `System.ServiceModel` channel objects. Neither is available in the ScenarioHost process (no SqlClient/ServiceModel dependency). Tests assert only that the command completes cleanly with the expected section structure.
