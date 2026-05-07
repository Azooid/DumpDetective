# weak-refs

**Category:** Heap / Memory  
**Included in `analyze --full`:** Yes

## What it does

Reports all live weak GC handles (short and long) and all `ConditionalWeakTable` instances on the heap. Useful for understanding object lifetime management, weak-reference-based caching infrastructure, and identifying leaks in patterns that are supposed to allow GC collection but are not.

Weak short handles are cleared when their target becomes unreachable and enters the finalizer queue. Weak long handles survive through finalization — they clear only after the finalizer runs. This difference matters: a long weak handle can still observe a finalizing object, and in some patterns it may be keeping one alive longer than expected.

---

## Analyzer: `WeakRefsAnalyzer`

**Class type:** Not `IHeapObjectConsumer` — reads the handle table and session cache  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

### How it works

**Weak handle enumeration** iterates `ctx.Runtime.EnumerateHandles()` and filters for `ClrHandleKind.WeakShort` and `ClrHandleKind.WeakLong`. For each handle, it checks whether `h.Object != 0` to determine if the target is still alive. If alive, the object's type name is resolved via `ctx.Heap.GetObject(h.Object)`. If the address is zero (cleared), the entry is recorded as `<collected>`.

**ConditionalWeakTable instances** are fetched from the session cache: `ctx.GetAnalysis<CwtData>()` — populated by `ConditionalWeakTableConsumer` during the main heap walk. On the slow path (no pre-warm), the analyzer walks `heap.EnumerateObjects()` looking for type names starting with `System.Runtime.CompilerServices.ConditionalWeakTable`. For each instance, it reads the internal `_container._entries` array length as the entry count (falling back to `_entries` directly for older .NET runtime layouts).

---

## Consumer: `ConditionalWeakTableConsumer`

**Class**: `DumpDetective.Analysis.Memory.Consumers.ConditionalWeakTableConsumer`  
**Implements**: `IHeapObjectConsumer`  
**Walk**: `DumpCollector.CollectHeapObjectsCombined`

### Per-object logic

The consumer is triggered only for objects whose `HeapTypeMeta.IsCwt` flag is set — this flag is computed once by `HeapWalker.BuildMeta` and cached per MethodTable. It is true when the type name starts with `System.Runtime.CompilerServices.ConditionalWeakTable`.

For matching objects, the consumer attempts to read the entry count via the `_container._entries` nested field path (the internal layout used by .NET 6 and later). If that fails, it falls back to reading `_entries` directly (the layout in older runtimes). Either way, it reads the array's `Length` as a proxy for the number of active key-value pairs.

The type parameter names (key and value types) are extracted from the generic suffix of the type name — the substring starting from the first `[` character.

### Clone and merge

`CreateClone()` returns a new empty consumer. `MergeFrom(other)` appends all entries from the clone's list, preserving all discovered instances from all parallel workers.

---

## Pre-warm path

`ConditionalWeakTableConsumer` runs during `DumpCollector.CollectHeapObjectsCombined`. `WeakRefsAnalyzer` reads `CwtData` from session cache.

---

## Disk cache

None.

---

## Options

| Option | Description |
|---|---|
| `--filter <text>` | Substring match on target object type name |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **Most `WeakShort` entries show `<collected>`** | Normal for a well-functioning weak-reference cache — objects are collected under memory pressure. |
| **All `WeakShort` entries alive** | Targets are strongly reachable elsewhere — the weak-reference eviction strategy isn't triggering. May indicate memory pressure is too low (development environment) or another strong reference path exists. |
| **`WeakLong` entries pointing to live objects** | Objects held via `WeakLong` survive through the finalizer queue. If these are application objects (not infrastructure), it may indicate unintended resurrection. |
| **Large `ConditionalWeakTable` entry counts** | Attached per-object metadata caches (common in EF Core, DI frameworks, runtime internals). If entry count is growing across dumps, keys are not being collected — the associated objects have strong references elsewhere. |
| **No collected entries at all** | If the dump was taken right after a GC cycle, all weak targets may still be alive. Normal after `GC.Collect()`. |

---

## Typical output shape

```
Weak GC Handles — 4,112 handles

Kind         Alive   Collected   Top Alive Types
WeakShort    2,814       1,298   System.String (1,441), MyApp.Models.User (881), ...
WeakLong         0           0   —

ConditionalWeakTable Instances — 3 found
  ConditionalWeakTable`2[ClrType, MetadataItem]     412 entries
  ConditionalWeakTable`2[DbContext, ChangeTracker]   88 entries
  ConditionalWeakTable`2[Object, ExtensionData]      12 entries
```
