# module-list

**Category:** Targeted / Interactive  
**Included in `analyze --full`:** Yes

## What it does

Lists every CLR assembly (module) loaded into the process at the time of the dump. Each entry is classified by origin (App, GAC, System, Dynamic) so you can instantly separate application code from infrastructure and identify unexpected or duplicate assemblies.

---

## Analyzer: `ModuleListAnalyzer`

**Class type:** Not `IHeapObjectConsumer` — reads CLR module metadata only  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

### How it works

Calls `ctx.Runtime.EnumerateModules()` — a cheap metadata enumeration with no heap walk. For each `ClrModule`, the file path and metadata size are read. Modules are classified into four categories:

- **Dynamic**: no file path (emitted at runtime via `Reflection.Emit` or Roslyn scripting)
- **GAC**: path contains `\GAC_MSIL\`, `\GAC_32\`, `\GAC_64\`, or `\assembly\GAC`
- **System**: filename starts with `System.`, `mscorlib`, or `netstandard`; or path contains `\dotnet\`, `Microsoft.NETCore`, or `\runtime\`
- **App**: everything else

Results are sorted with App first, then GAC, then System and Dynamic, alphabetically within each group.

---

## Pre-warm path

`ctx.GetAnalysis<ModuleListData>()` — if already run (e.g. by `analyze --full`), returns the session-cached result. Module enumeration is < 50 ms even on large dumps, but caching avoids redundant work in parallel sub-reports.

---

## Disk cache

None. Module metadata is cheap to re-read and is available directly from the CLR runtime structures.

---

## Options

| Option | Description |
|---|---|
| `--filter <text>` | Case-insensitive substring match on the full module path or filename |
| `--app-only` | Show only `App`-classified modules |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **`Dynamic` modules** | Code emitted at runtime via `Reflection.Emit` or Roslyn scripting. Expected in small numbers from DI containers (Castle.Core, Autofac, Unity, etc.). A spike in dynamic module count after a memory event may indicate a code generation loop. |
| **Duplicate assembly filenames at different paths** | Binding redirect misconfiguration, shadow-copy issue, or the same assembly loaded from GAC and from the app's bin directory simultaneously. |
| **Old version assemblies** | If `Path.GetFileName` + assembly version mismatch what's deployed — stale assemblies in GAC or shadow copy directory. |
| **Large metadata `Size` on App modules** | Very reflection-heavy assemblies. Not a memory leak but contributes to startup and JIT time. |
| **Unexpected third-party assemblies in App group** | Dependency injection pulling in indirect references you didn't expect to be loaded. Audit for security and licensing compliance. |
| **GAC modules where App is expected** | Assembly accidentally registered in the GAC — behavior may differ from deployed version. |

---

## Typical output shape

```
Module List — 312 modules loaded

Kind    File Name                          Path                                         Size
App     MyService.dll                      D:\app\bin\MyService.dll                     2.1 MB
App     MyService.Data.dll                 D:\app\bin\MyService.Data.dll                850 KB
App     MyService.Api.dll                  D:\app\bin\MyService.Api.dll                 320 KB
GAC     System.Web.dll                     C:\Windows\Microsoft.NET\...                 6.4 MB
System  System.Runtime.dll                 C:\Program Files\dotnet\shared\...           412 KB
System  System.Text.Json.dll               C:\Program Files\dotnet\shared\...           880 KB
Dynamic <dynamic>                          —                                            0 B
```
