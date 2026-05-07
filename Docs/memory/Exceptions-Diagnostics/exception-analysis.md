# exception-analysis

**Category:** Exceptions / Diagnostics  
**Included in `analyze --full`:** Yes

## What it does

Finds all live exception objects on the managed heap, groups them by type, and for each type captures sample instances with their message, HResult, inner exception type, and stack trace. Identifies exception storms, swallowed exceptions stored in `Task` results, and repeated failures. The presence of thousands of exception objects on the heap is always noteworthy — exceptions are expensive to allocate and should not be accumulating.

---

## Analyzer: `ExceptionAnalysisAnalyzer`

**Implements:** `IHeapObjectConsumer` directly  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

### How it works

`ExceptionAnalysisAnalyzer` implements `IHeapObjectConsumer` and runs directly in the heap walk alongside `ExceptionCountConsumer`.

For each heap object, the `HeapTypeMeta.IsException` flag is checked — set when the type's ClrMD `IsException` property is true (i.e. the type inherits from `System.Exception`). Non-exception objects are skipped with a single flag check, incurring no overhead.

For matching objects, the total count is always incremented. If fewer than 10 samples have been collected for this type, the analyzer reads four fields: `_message` for the exception message string, `_HResult` for the integer error code, `_innerException` for the inner exception's type name (one level deep only), and `_stackTrace` for the raw stack trace. The `_stackTrace` field changed layout in .NET 5+ — it is an object rather than a string, so the analyzer tries `ReadStringField("_stackTrace")` first and falls back to `ReadObjectField("_stackTrace").AsString(512)` if that fails.

All field reads are wrapped in try/catch. Corrupted exception objects (common in OOM or crash dumps) are silently skipped.

After the walk, groups are sorted by count descending.

---

## Consumer: `ExceptionCountConsumer`

**Class**: `DumpDetective.Analysis.Memory.Consumers.ExceptionCountConsumer`  
**Implements**: `IHeapObjectConsumer`  
**Walk**: `DumpCollector.CollectHeapObjectsCombined`  
**Purpose**: Counts live exception instances per type during the shared walk — no field reads, no message capture. Feeds `DumpSnapshot.ExceptionCount` for health scoring.

### Two-consumer pattern

Both `ExceptionCountConsumer` and `ExceptionAnalysisAnalyzer` run in the same combined heap walk:
- `ExceptionCountConsumer` does only `Totals[typeName]++` per exception object — a dictionary increment with no field access. This is always active and feeds the health score.
- `ExceptionAnalysisAnalyzer` does the full field read (message, HResult, inner type, stack trace). This is activated on demand or in `analyze --full`.

This split ensures that even in lightweight mode (just `load`, no full analysis), the health scorer knows how many exceptions are on the heap.

---

## Pre-warm path

`ExceptionCountConsumer` (lightweight — count only, no message reads) runs during `DumpCollector.CollectHeapObjectsCombined`. The full `ExceptionAnalysisAnalyzer` (with message/stack capture) runs on demand or in `analyze --full`. Its result is then cached in session.

---

## Disk cache

None.

---

## Options

| Option | Description |
|---|---|
| `--filter <text>` | Substring match on exception type name or message |
| `--top <n>` | Show top N types by count (default: all) |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **Large count of one exception type** | Exception storm — the same failure occurring repeatedly. If they're being caught and stored in `Task.Exception` or error collections, they accumulate on the heap. |
| **`System.OutOfMemoryException`** | Process hit memory pressure during execution. Combined with `heap-stats`, identify what consumed the memory. |
| **`TaskCanceledException` / `OperationCanceledException` in thousands** | Mass cancellation — requests timing out or being cancelled faster than they complete. Check `async-stacks` for pending operations. |
| **`SocketException` / `HttpRequestException` / `SqlException`** | Infrastructure failures — network timeouts, DB disconnects. Check `http-requests`, `connection-pool`. |
| **`AggregateException` wrapping the same inner type** | Async code re-wrapping the same failure repeatedly. Look at the `InnerExType` field in samples. |
| **Stack trace identifies a specific method** | The sample stack traces pinpoint the exact failure site — no debugger required. |
| **HResult = `0x80070005` (E_ACCESSDENIED)** | Permissions failure — file, registry, or COM access denied. |
| **HResult = `0x8007000E` (E_OUTOFMEMORY)** | Native memory exhaustion, not just managed heap. |

---

## Typical output shape

```
Exception Analysis — 48,221 live exception objects  •  14 types

Type                                        Count   Sample Message
System.TimeoutException                    41,882   The operation has timed out.
System.Net.Sockets.SocketException          4,112   Connection refused (127.0.0.1:5432)
System.Data.SqlClient.SqlException          1,884   Timeout expired. The timeout period elapsed...
System.InvalidOperationException              312   Collection was modified; enumeration may not execute.
System.NullReferenceException                  31   Object reference not set to an instance of an object.
```
