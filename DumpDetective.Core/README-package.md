# DumpDetective.Core

The plugin API surface for [DumpDetective](https://github.com/Azooid/DumpDetective) — a .NET 10 CLI tool for analysing Windows memory dumps (`.dmp` / `.mdmp`) and EventPipe / ETW trace files (`.nettrace` / `.etl`).

Reference this package from your plugin project to implement `ICommand`, `ITracePlugin`, and `IPluginManifest` without shipping any DumpDetective assemblies alongside your plugin DLL.

## Quick start

```xml
<!-- MyPlugin.DumpDetective.csproj -->
<ItemGroup>
  <!--
    ExcludeAssets="runtime" keeps the host's DLLs out of your plugin output folder.
    The host provides all Core (and transitive ClrMD / TraceEvent) assemblies at runtime.
  -->
  <PackageReference Include="DumpDetective.Core" Version="3.0.1" ExcludeAssets="runtime" />
</ItemGroup>
```

```csharp
// Manifest.cs
public sealed class Manifest : IPluginManifest
{
    public string  PluginName => "MyPlugin.DumpDetective";
    public string? Version    => "1.0.0";

    public IEnumerable<ICommand> RegisterCommands()
    {
        yield return new MyDumpCommand();
        yield return new MyTraceCommand();   // optionally also implements ITracePlugin
    }
}
```

## Key interfaces

| Interface | Purpose |
|---|---|
| `IPluginManifest` | Entry point — `RegisterCommands()` called once at startup |
| `ICommand` | Memory or trace command; `Run(args)` for CLI, `Render(ctx, sink)` for `analyze --full` |
| `ITracePlugin` | Lightweight trace sub-analyzer; participates in `trace-analyze --with-plugins` |
| `IRenderSink` | Format-agnostic output (HTML, Markdown, JSON, plain text, console) |

## Plugin installation

Drop the output folder next to the host executable or in the user-profile plugins directory:

```
<exe dir>/plugins/MyPlugin.DumpDetective/MyPlugin.DumpDetective.dll
%USERPROFILE%\.dumpdetective\plugins\MyPlugin.DumpDetective\MyPlugin.DumpDetective.dll
```

Only your plugin DLL is needed — all transitive host assemblies are resolved at runtime.

## More information

Full plugin authoring guide: [Docs/Plugins.md](https://github.com/Azooid/DumpDetective/blob/main/Docs/Plugins.md)

Example plugin with four commands (memory + trace): [`Docs/PluginExample/Example.DumpDetective/`](https://github.com/Azooid/DumpDetective/tree/main/Docs/PluginExample/Example.DumpDetective)
