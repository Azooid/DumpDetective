# Extending DumpDetective — Plugin System

DumpDetective supports external plugins: self-contained .NET class libraries that add new analysis commands discoverable alongside the built-in ones. No changes to the host binary are required.

---

## How it works

At startup the host scans two directories for plugin sub-folders:

| Priority | Location |
|---|---|
| 1 (first) | `<exe dir>/plugins/` — side-by-side with the executable |
| 2 (fallback) | `%USERPROFILE%\.dumpdetective\plugins\` (or `$HOME/.dumpdetective/plugins/` on Linux/Mac) |

Each sub-folder is treated as one plugin. The folder name must match the main DLL name:

```
plugins/
  MyPlugin.DumpDetective/
    MyPlugin.DumpDetective.dll   ← entry DLL; name must match folder
    MyHelper.dll                 ← private dependency (optional)
    ...
```

The loader finds the single concrete class that implements `IPluginManifest`, instantiates it, and calls `RegisterCommands()` once. The returned `ICommand` instances are registered alongside all built-in commands. Duplicate command names are silently skipped (first match wins, host commands always win).

Each plugin runs in its own `AssemblyLoadContext`. Private dependencies never conflict with the host or other plugins. Host assemblies (`DumpDetective.*`, ClrMD, TraceEvent, Spectre.Console, `System.*`, `Microsoft.*`) are resolved from the host's default context so types are shared — your plugin's `ICommand` is the same interface the host dispatches.

---

## Plugin cache integration

Plugins can use the same `DumpContext` analysis-cache model as built-in commands.

Custom plugin caches require only `DumpDetective.Core`. Reusing host-owned memory cache types such as `BfsCacheBox` requires an additional reference to the assembly that defines that cache type.

### Reuse an existing cache

If a built-in command or the host has already populated a cache, read it from `DumpContext`:

```csharp
var threadNames = ctx.GetAnalysis<ThreadNameMap>();
var bfsBox   = ctx.GetOrCreateAnalysis<BfsCacheBox>(
    () => new BfsCacheBox(BfsIndexCache.TryLoad(ctx.DumpPath, null)));
var bfs      = bfsBox.Cache;
```

- `GetAnalysis<T>()` returns a previously stored value or `null`.
- `GetOrCreateAnalysis<T>()` computes once and shares the result across parallel sub-reports.
- Use the same `T` consistently when reading and writing the cache entry.

### Create a plugin-owned cache

For plugin-specific state, create your own POCO cache type and store it in `DumpContext`:

```csharp
public sealed record MyPluginCache(IReadOnlyList<MyRow> Rows);

var cache = ctx.GetOrCreateAnalysis(() => BuildMyPluginCache(ctx));
```

This is the standalone pattern: your command can build the cache on demand when it runs by itself.

### Participate in the shared heap walk

To avoid a second heap walk during `analyze --full --with-plugins`, implement `ICommandHeapContributor` on your command:

```csharp
public sealed class MyPluginCommand : ICommand, ICommandHeapContributor
{
    private readonly MyPluginConsumer _consumer = new();

    public IReadOnlyList<IHeapObjectConsumer> CreateHeapConsumers()
        => [_consumer];

    public void PublishResults(DumpContext ctx)
        => ctx.SetAnalysis(new MyPluginCache(_consumer.Rows));
}
```

How this works:

- Standalone command run: your command still uses `ctx.GetOrCreateAnalysis<T>()` and can build the cache itself.
- `analyze --full --with-plugins`: the host calls `CreateHeapConsumers()`, adds them to the shared `HeapWalker.Walk(...)`, then calls `PublishResults(ctx)` before sub-reports start.
- Your later `BuildReport` call reads the already-populated cache from `DumpContext` instead of walking the heap again.

### Keep a cache alive during a batch

If your plugin depends on a cache that must not be released until the batch finishes, implement `ICommandCachePin`:

```csharp
public sealed class MyPluginCommand : ICommand, ICommandCachePin
{
    public IReadOnlyList<Type> PinnedCacheTypes =>
    [
        typeof(BfsCacheBox),
        typeof(MyPluginCache),
    ];
}
```

The host pins those cache types before parallel sub-reports run and unpins them afterward. This is the correct way to say "I will use this cache later in the batch; do not release it yet." Use it for caches that are preloaded on the main thread and explicitly released after sub-reports, such as `BfsCacheBox`.

### Recommended plugin pattern

1. Build your cache with `ctx.GetOrCreateAnalysis<T>()` so standalone runs work.
2. If the cache comes from a heap walk, also implement `ICommandHeapContributor` so full-analysis can pre-populate it during the shared walk.
3. If the cache must survive an orchestrated batch cleanup step, implement `ICommandCachePin` and list the cache entry types you depend on.
4. Do not manually clear host caches from plugin code. Let the orchestrator manage cache release.

---

## Plugin participation modes

| Mode | Interface | Triggered by | Minimum dependency |
|---|---|---|---|
| Standalone memory command | `ICommand` | `DumpDetective my-command app.dmp` | `DumpDetective.Core` |
| Included in `analyze --full` | `ICommand` with `IncludeInFullAnalyze = true` | `analyze --full --with-plugins` | `DumpDetective.Core` |
| Standalone trace command | `ICommand` with `Kind = CommandKind.Trace` | `DumpDetective my-trace-cmd perf.etl` | `DumpDetective.Core` |
| Trace sub-analyzer (simple) | `ICommand` + `ITracePlugin` | `trace-analyze --with-plugins` | `DumpDetective.Core` only |
| Trace sub-analyzer (advanced) | `ICommand` + `ITraceSubAnalyzer` | `trace-analyze --with-plugins` | `DumpDetective.Commands` |

`ITracePlugin` (in `DumpDetective.Core`) is the recommended interface for most trace plugins — it requires only Core, making the plugin suitable for NuGet distribution without any compile-time dependency on host command assemblies.

`ITraceSubAnalyzer` (in `DumpDetective.Commands.Trace`) is the advanced interface used by the built-in trace commands. It supports the consumer-based single-pass dispatch pipeline (`SupportsConsumer`) and correlation phases — useful if you want to participate in the same event-dispatch loop as built-in analyzers to avoid re-scanning the trace.

---

## Quick start

### 1 — Create the project

```xml
<!-- MyPlugin.DumpDetective.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- Folder and DLL name must match -->
    <AssemblyName>MyPlugin.DumpDetective</AssemblyName>
    <RootNamespace>MyPlugin.DumpDetective</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <!--
      Reference Core only.
      Private="false" means the host's copy is NOT duplicated in the
      plugin output folder — the host provides it at runtime.
      TraceLog and other TraceEvent types are available transitively
      through Core's compile-time TraceEvent reference — no separate
      package reference needed.
    -->
    <ProjectReference Include="path\to\DumpDetective.Core\DumpDetective.Core.csproj"
                      Private="false" />
  </ItemGroup>

  <!--
    Optional: auto-deploy to the host's plugins folder on every build.
    Override PluginDeployDir to point at the installed exe's plugins folder.
  -->
  <PropertyGroup>
    <PluginDeployDir Condition="'$(PluginDeployDir)'==''">path\to\DumpDetective\plugins\$(AssemblyName)\</PluginDeployDir>
  </PropertyGroup>

  <Target Name="DeployPlugin" AfterTargets="Build">
    <MakeDir Directories="$(PluginDeployDir)" />
    <ItemGroup>
      <_PluginFiles Include="$(OutputPath)*"
                    Exclude="$(OutputPath)*.pdb;$(OutputPath)*.runtimeconfig.json;$(OutputPath)*.deps.json;$(OutputPath)DumpDetective.*.dll" />
    </ItemGroup>
    <Copy SourceFiles="@(_PluginFiles)" DestinationFolder="$(PluginDeployDir)" SkipUnchangedFiles="true" />
    <Warning Text="Plugin deployed → $(PluginDeployDir)" />
  </Target>
</Project>
```

> **NuGet workflow**: If building against a published NuGet package rather than the source tree, use `<PackageReference Include="DumpDetective.Core" ExcludeAssets="runtime" />`. Only Core is needed; do not add a direct TraceEvent package reference — it flows in from Core.

### 2 — Implement `IPluginManifest`

```csharp
using DumpDetective.Core.Interfaces;

public sealed class Manifest : IPluginManifest
{
    public string  PluginName => "MyPlugin.DumpDetective";
    public string? Version    => "1.0.0";

    public IEnumerable<ICommand> RegisterCommands()
    {
        yield return new MyDumpCommand();
        yield return new MyTraceCommand();   // implements ITracePlugin
    }
}
```

### 3a — Memory dump command (`ICommand`)

```csharp
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;

// CommandBase is a STATIC utility class — do not inherit from it.
// Implement ICommand directly and call CommandBase.* methods.
public sealed class MyDumpCommand : ICommand
{
    public string Name                 => "my-analysis";
    public string Description          => "Does something useful with the dump.";
    public bool   IncludeInFullAnalyze => true;    // included in `analyze --full --with-plugins`
    public string Category             => "My Plugin";
    public CommandKind Kind            => CommandKind.Memory;

    private const string Help = """
        Usage: DumpDetective my-analysis <dump.dmp> [options]
        Does something useful.
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;
        return CommandBase.Execute(CliArgs.Parse(args), (ctx, sink) =>
        {
            sink.Header("My Analysis", CommandBase.Subtitle(ctx));
            sink.Section("Results");

            var heap = ctx.Runtime.Heap;
            int count = 0;
            foreach (var obj in heap.EnumerateObjects())
            {
                if (obj.Type?.Name == "MyApp.ExpensiveObject") count++;
            }
            sink.KeyValues([("ExpensiveObject count", count.ToString("N0"))]);
        });
    }

    public void Render(DumpContext ctx, IRenderSink sink)
    {
        sink.Header("My Analysis", CommandBase.Subtitle(ctx));
        sink.Section("Results");
        // analysis logic here
    }
}
```

> **`BuildReport`**: the default `ICommand.BuildReport` implementation calls `CommandBase.ReportDocBuilder` which is wired by `ReportingBootstrap.Register()` at host startup. You do not need to override it unless you want explicit control.

### 3b — Trace command (`ICommand` + `ITracePlugin`)

`ITracePlugin` is in `DumpDetective.Core` — no extra project reference needed. The orchestrator handles the `CaptureSink` wrap and section header; your `Analyze` method just writes to the provided `IRenderSink`.

```csharp
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Tracing;          // TraceEvent — available via Core transitively
using Microsoft.Diagnostics.Tracing.Etlx;    // TraceLog
using Spectre.Console;

public sealed class MyTraceCommand : ICommand, ITracePlugin
{
    // ICommand
    public string Name                 => "my-trace";
    public string Description          => "Inventories something in a trace file.";
    public bool   IncludeInFullAnalyze => false;   // trace command — not for dump full-analyze
    public string Category             => "My Plugin";
    public CommandKind Kind            => CommandKind.Trace;

    // ITracePlugin — participates in `trace-analyze --with-plugins`
    public string Key          => Name;
    public string SectionTitle => "My Trace Section";

    public string? Analyze(TraceLog trace, string traceFileName,
                           int top, string? processFilter, IRenderSink sink)
    {
        // trace is already open — do not dispose it.
        // sink already has a Header written by the orchestrator.
        long count = 0;
        foreach (TraceEvent e in trace.Events)
        {
            if (e.ProviderName == "MyCompany-Events") count++;
        }

        sink.KeyValues([
            ("Trace file", traceFileName),
            ("MyCompany events", count.ToString("N0")),
        ]);

        return $"{count:N0} events";   // short summary shown in console status line
    }

    // Standalone: opens the trace directly when run as its own command.
    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var a         = CliArgs.Parse(args);
        string? path  = a.DumpPath ?? a.Positionals.FirstOrDefault();

        if (path is null || !File.Exists(path))
        {
            AnsiConsole.MarkupLine("[red]✗[/] Trace file required.");
            return 1;
        }

        var outputPaths = a.EffectiveOutputPaths.Count > 0
            ? a.EffectiveOutputPaths
            : (IReadOnlyList<string>)[CommandBase.DefaultOutputPath(path, ".html")];

        using var sink = SinkFactory.CreateMulti(outputPaths);
        using TraceEventDispatcher source = path.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase)
            ? new EventPipeEventSource(path)
            : new ETWTraceEventSource(path);

        long count = 0;
        source.Dynamic.All += e =>
        {
            if (e.ProviderName == "MyCompany-Events") count++;
        };
        source.Process();

        sink.Header(SectionTitle, Path.GetFileName(path), navLevel: 2);
        sink.KeyValues([("MyCompany events", count.ToString("N0"))]);
        return 0;
    }

    // Memory dump is not applicable.
    public void Render(DumpContext ctx, IRenderSink sink) =>
        sink.Alert(AlertLevel.Info, $"{Name} requires a trace file, not a memory dump.");

    private const string Help = """
        Usage: DumpDetective my-trace <trace-file> [options]
        Also participates in 'trace-analyze --with-plugins'.
        """;
}
```

### 4 — Build and install

`dotnet build` deploys automatically if you added the `DeployPlugin` target (see step 1). If you didn't, copy the output manually:

```bash
dotnet build MyPlugin.DumpDetective -c Release
# DeployPlugin target fires here → DLL lands in PluginDeployDir automatically

# Manual install (only needed without the DeployPlugin target)
dotnet build MyPlugin.DumpDetective -c Release -o out/

# Side-by-side install (next to the exe)
xcopy /E out\ "<exe dir>\plugins\MyPlugin.DumpDetective\"

# OR user-profile install (works across tool updates)
xcopy /E out\ "%USERPROFILE%\.dumpdetective\plugins\MyPlugin.DumpDetective\"
```

> **Only 1 DLL needed.** Because `Private="false"` suppresses all transitive host assemblies from the plugin output, your plugin folder contains only your own DLL. The host resolves all shared assemblies from its own bundle at runtime.

### 5 — Verify

```bash
DumpDetective --version          # shows loaded plugin names
DumpDetective --help             # my-analysis and my-trace appear under "My Plugin"
DumpDetective my-analysis app.dmp
DumpDetective my-trace perf.etl

# Participate in orchestrators:
DumpDetective analyze app.dmp --full --with-plugins    # includes my-analysis
DumpDetective trace-analyze perf.etl --with-plugins    # includes my-trace via ITracePlugin
```

---

## `IPluginManifest` reference

```csharp
public interface IPluginManifest
{
    /// Human-readable plugin name shown in --version output.
    string PluginName { get; }

    /// Optional version string (e.g. "1.2.3").  Shown in --version output.
    string? Version { get; }

    /// Called once at startup.  Return all commands the plugin provides.
    IEnumerable<ICommand> RegisterCommands();
}
```

---

## `ICommand` reference

```csharp
public interface ICommand
{
    string      Name;                // CLI name, e.g. "my-analysis"
    string      Description;         // one-line help text
    bool        IncludeInFullAnalyze;// included in `analyze --full --with-plugins`
    string      Category;            // group heading in --help output
    CommandKind Kind;                // Memory | Trace

    int         Run(string[] args);                        // CLI entry point
    void        Render(DumpContext ctx, IRenderSink sink); // called by analyze --full
    ReportDoc   BuildReport(DumpContext ctx);              // default via ReportDocBuilder
}
```

Helpers in `CommandBase` (static class in `DumpDetective.Core.Utilities`):

| Helper | What it does |
|---|---|
| `Execute(CliArgs, Action<DumpContext, IRenderSink>)` | Opens dump, sets up sink, handles errors, returns exit code |
| `TryHelp(args, helpText)` | Prints help and returns `true` if `--help` / `-h` is present |
| `Subtitle(ctx)` | Formats the standard `"<dump-name>  ·  <timestamp>"` subtitle string |
| `RunStatus(label, work)` | Shows a live spinner while `work` runs |
| `DefaultOutputPath(path, ext)` | Derives `<path-without-ext>.<ext>` |

---

## `ITracePlugin` reference

The simple trace sub-analyzer interface. Lives in `DumpDetective.Core` — no extra project reference required.

```csharp
public interface ITracePlugin
{
    string Key { get; }            // section key, e.g. "my-trace"
    string SectionTitle { get; }   // section heading in the combined report

    /// Called once per trace file by the orchestrator when --with-plugins is set.
    /// The orchestrator has already written a Header to the sink.
    /// trace: already-open TraceLog — do not dispose.
    /// Returns a short summary string for console display, or null.
    string? Analyze(TraceLog trace, string traceFileName,
                    int top, string? processFilter, IRenderSink sink);
}
```

The orchestrator (`trace-analyze`, `trace-dump-analyze`) wraps each `ITracePlugin.Analyze` call in a `CaptureSink`, writes a `Header`, calls `Analyze`, then replays the captured `ReportDoc` into the combined report's **Plugins** section.

---

## `ITraceSubAnalyzer` reference (advanced)

For plugins that need to participate in the host's single-pass event-dispatch loop (to avoid re-opening the trace file), implement `ITraceSubAnalyzer` from `DumpDetective.Commands.Trace` instead. This requires referencing `DumpDetective.Commands` (compile-only, `Private="false"`).

Key additional capabilities over `ITracePlugin`:

| Capability | Interface member |
|---|---|
| Single-pass consumer dispatch | `SupportsConsumer`, `CreateConsumer`, `CompleteFromConsumer` |
| Per-event routing filter | `ITraceEventConsumer.WantsEvent(eventName)` |
| Post-correlation phase | `HasCorrelationPhase`, `OnCorrelationAvailable` |
| Dump-available hook | `OnDumpAvailable` (trace-dump-analyze only) |

Use `ITracePlugin` unless you specifically need one of these capabilities — it keeps your project dependency surface minimal.

---

## Output pipeline

Use `IRenderSink` methods to write structured output. Both `ICommand.Render` and `ITracePlugin.Analyze` receive a sink:

```csharp
sink.Section("Top Results");
sink.Table(["Name", "Count", "Size"],
    rows.Select(r => new[] { r.Name, r.Count.ToString("N0"), FormatSize(r.Size) }).ToList(),
    caption: $"{rows.Count} items");

sink.Alert(AlertLevel.Warning, "Something looks off.", "Detail here.");
sink.KeyValues([("Total", "42"), ("Duration", "1.2 s")]);

sink.BeginDetails("Expanded breakdown", open: false);
sink.Table(["Key", "Value"], detailRows);
sink.EndDetails();
```

`IRenderSink` is in `DumpDetective.Core.Interfaces` — available from Core alone.

---

## Dependency isolation

The `PluginLoadContext` resolves assemblies as follows:

| Assembly | Resolved from |
|---|---|
| `DumpDetective.*` | Host default context (shared type identity) |
| `Microsoft.Diagnostics.*` (ClrMD, TraceEvent) | Host default context |
| `Spectre.*`, `System.*`, `Microsoft.*` | Host default context |
| Everything else | Plugin's own directory |

This means your plugin can depend on any NuGet package without worrying about version conflicts with other plugins or the host — as long as you don't ship competing copies of the host's core assemblies.

---

## Example project

A working example is provided at [`Docs/PluginExample/Example.DumpDetective/`](PluginExample/Example.DumpDetective/). Copy it as a starting point:

```
Docs/
  PluginExample/
    Example.DumpDetective/
      Example.DumpDetective.csproj   ← references Core only (Private="false")
      Manifest.cs                    ← IPluginManifest
      DuplicateModulesCommand.cs     ← ICommand (memory dump, IncludeInFullAnalyze=true)
      NamespaceHeapCommand.cs        ← ICommand (memory dump, IncludeInFullAnalyze=true)
      ThreadHotspotsCommand.cs       ← ICommand (memory dump, IncludeInFullAnalyze=true)
      TraceEventInventoryCommand.cs  ← ICommand + ITracePlugin (trace sub-analyzer)
```

**`duplicate-modules`** — Scans all loaded CLR modules and flags assemblies that appear more than once — exact duplicates (same path, two app domains) or the same name at different file paths (version conflict). Run with `analyze --full --with-plugins` or standalone.

**`namespace-heap`** — Walks the heap and groups objects by top-level namespace (e.g. `Newtonsoft`, `System`, `MyApp`). Reports size, % of heap, and object count per namespace, with a donut chart of the top 8. A fast first pass when memory is high and you don’t know which library to blame.

**`thread-hotspots`** — Groups all managed threads by their topmost managed stack frame. High counts at a single frame indicate saturation, a blocking call, or a deadlock. Pass `--details` to see the full stack trace for each thread in the top groups.

**`trace-inventory`** — Inventories every event provider and event name in a `.nettrace` or `.etl` file — total events, trace duration, and the top N providers ranked by volume. Participates in `trace-analyze --with-plugins` via `ITracePlugin` and also runs standalone.

Build and install:

```bash
# Build deploys automatically via the DeployPlugin MSBuild target
dotnet build Docs/PluginExample/Example.DumpDetective -c Release

# To deploy to a different location (e.g. the published exe's plugins folder):
dotnet build Docs/PluginExample/Example.DumpDetective -c Release \
  -p:PluginDeployDir="path\to\publish\plugins\Example.DumpDetective\"

# Standalone usage
DumpDetective duplicate-modules app.dmp
DumpDetective namespace-heap app.dmp --top 30
DumpDetective thread-hotspots app.dmp --details
DumpDetective trace-inventory perf.etl --top 30

# Orchestrator usage (opt-in via --with-plugins)
DumpDetective analyze app.dmp --full --with-plugins           # includes duplicate-modules
DumpDetective trace-analyze perf.etl --with-plugins           # includes trace-inventory
DumpDetective trace-dump-analyze perf.etl app.dmp --with-plugins
```

---

## `--with-plugins` flag

Plugin commands are **excluded by default** from orchestrator runs. This is intentional — it prevents untrusted or slow plugins from affecting production triage runs without explicit opt-in.

| Orchestrator | Flag | Effect |
|---|---|---|
| `analyze` | `--with-plugins` | Appends plugin commands with `IncludeInFullAnalyze = true` to `--full` run |
| `trend-analysis` | `--with-plugins` | Same as analyze, applied per dump |
| `trace-analyze` | `--with-plugins` | Appends `ITracePlugin` and `ITraceSubAnalyzer` plugin commands |
| `trace-dump-analyze` | `--with-plugins` | Same as trace-analyze |

When `--with-plugins` is set and at least one plugin command qualifies, a log line appears after the file is loaded:

```
[17:05:24]   ✅ Dump loaded  |  CLR 4.8.4795.0
[17:05:24]     ℹ + 1 plugin command(s) included via --with-plugins: duplicate-modules
```

---

## Limitations

- **Partial trim only.** The single-file self-contained publish uses `TrimMode=partial`, which trims only `DumpDetective.*` assemblies and leaves all .NET framework assemblies (including `System.Runtime.dll` and other facades) untouched. This is required because plugins reference those framework facades at type-load time. Full trimming (`TrimMode=full`) would remove those facades and cause all plugins to fail at load (`ReflectionTypeLoadException: System.Runtime not found`).
- **No AOT in the host.** The host disables AOT when publishing with a runtime identifier (`PublishAot=false` when `RuntimeIdentifier != ''`). AOT removes the managed type system that plugin loading depends on.
- **No AOT in plugins.** Plugin projects must be regular .NET class libraries. They cannot be AOT-compiled.
- **Net version.** Plugins must target the same major .NET version as the host (`net10.0`).
- **No source generators.** Avoid `[JsonSerializable]` source generators in plugins — AOT JSON contexts are compiled into each assembly and are not shared across the ALC boundary.

---

## Error handling

Failed plugins are logged as warnings and skipped — they do not crash the host:

```
Plugin warning: BadPlugin.DumpDetective failed to load — Could not find file '...'
```

Common causes:

| Error | Fix |
|---|---|
| `no IPluginManifest implementation found` | Add a class implementing `IPluginManifest` to your assembly |
| `Could not find file '...'` | Folder name and DLL name don't match, or the build output is in a different directory |
| Type cast exception at command dispatch | You shipped a copy of `DumpDetective.Core.dll` alongside your plugin — use `Private="false"` on the project reference (or `ExcludeAssets="runtime"` for package references) |
| `trace-inventory` not appearing in `trace-analyze --with-plugins` | Command must implement `ITracePlugin` (or `ITraceSubAnalyzer`); `ICommand`-only trace commands run standalone only |
