# closure-capture

**Category:** Retention / Leak Signals  
**Included in `analyze --full`:** Yes

## What it does

Walks the managed heap for compiler-generated closure display-class objects (`<>c__DisplayClass*`). Groups by declaring type, computes retained size via BFS, and reports the captured field types for the top groups. A large closure object graph usually means an async callback or LINQ expression is holding references that outlive their expected scope.

---

## Analyzer: `ClosureCaptureAnalyzer`

**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

Iterates all objects whose type name matches the `<>c__DisplayClass` or `<>c__` prefix pattern. For each instance it walks declared fields to identify what is captured: strings, arrays, or reference-type objects above a size threshold. BFS is used to compute retained size. Results are grouped by declaring parent type (extracted from the compiler-generated name) and sorted by total retained size descending.

### Consumer: `ClosureCaptureConsumer`

Pre-screens during the shared heap walk, counting closure instances per MethodTable so the analyzer can skip types with no closures.

---

## Options

| Option | Description |
|---|---|
| `<dump>` | Path to `.dmp` or `.mdmp` file |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |
| `-h, --help` | Show this help |

---

## What to look for

| Signal | What it means |
|---|---|
| Closure with large array or collection field | Async callback captured a data buffer it no longer needs |
| Many closures all from the same declaring type | Hot code path creating closures in a loop without releasing them |
| Closure retained size > instance size by 10× | Closure is the only root keeping a large graph alive |
| Closures from ASP.NET middleware / DI types | Request-scoped objects captured in a singleton lambda |
