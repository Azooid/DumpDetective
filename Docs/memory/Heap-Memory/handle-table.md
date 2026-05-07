# handle-table

**Category:** Heap / Memory  
**Included in `analyze --full`:** Yes

## What it does

Summarizes the entire GC handle table by handle kind — counts, total referenced memory, and a per-type breakdown per kind. Every GC root that is not a stack variable is represented here: static-field proxies, COM wrappers, explicit application roots, async I/O pins, dependent handles, and more. This command gives a complete inventory of why objects are being kept alive from outside the managed heap's normal reference graph.

---

## Analyzer: `HandleTableAnalyzer`

**Class type:** Not `IHeapObjectConsumer` — reads the GC handle table directly  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

Enumerates `ctx.Runtime.EnumerateHandles()` and for each handle reads the `HandleKind` enum value and, if the target address is non-zero, resolves the target object's type name and size via `ctx.Heap.GetObject(h.Object)`. Accumulates per-kind totals using `CollectionsMarshal.GetValueRefOrAddDefault` and, for each kind, builds a type-name frequency dictionary. After enumeration, the top 5 types per kind are extracted by sorting the per-kind dictionary. A spinner updates every ~200ms for large handle tables.

---

## Disk cache

None.

---

## Options

| Option | Description |
|---|---|
| `--filter <kind>` | Show only the specified handle kind (e.g. `Strong`, `Pinned`, `WeakShort`) |

---

## GC Handle Kind Reference

| Kind | Meaning |
|---|---|
| `Strong` | Standard GC root — prevents collection. Used for statics, COM wrappers, explicit `GCHandle.Alloc(Normal)`. |
| `WeakShort` | Cleared when object becomes finalizable (pre-finalization). Standard `WeakReference`. |
| `WeakLong` | Cleared after finalization completes. `ConditionalWeakTable` secondary entries. |
| `Pinned` | Explicit pin via `GCHandle.Alloc(Pinned)`. Prevents object movement during GC compaction. |
| `AsyncPinned` | Runtime-created pin for overlapped I/O. Freed when the I/O operation completes. |
| `Dependent` | Keeps the dependent object alive as long as the primary is alive. `ConditionalWeakTable` primary entries. |
| `SizedRef` | ASP.NET cache infrastructure — tracks memory size of a cache partition. |
| `RefCounted` | COM interop ref-counted handle. Released via `Marshal.ReleaseComObject`. |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **Thousands of `Strong` handles** | Many explicit GC roots. Each strong handle prevents its entire reachability subgraph from being collected. |
| **Large Pinned or AsyncPinned count** | See `pinned-objects` for per-object detail. |
| **Many `Dependent` handles** | `ConditionalWeakTable` entries — attached metadata per object. If count is unexpectedly large, CWT entries are accumulating. |
| **`SizedRef` handles present** | ASP.NET's application cache (`HttpRuntime.Cache`) is active and using memory budget tracking. |
| **Total handle count in millions** | Handle table leak — `GCHandle` objects not being freed via `h.Free()`. COM wrapper not being released. |
| **Large `RefCounted` group** | COM interop objects retained — verify `Marshal.ReleaseComObject` is called and reference counts reach zero. |

---

## Typical output shape

```
GC Handle Table — 82,441 handles total

Kind           Count    Total Size   Top Types
Strong        71,882    8.4 GB      System.String (12,441), MyApp.Services.X (4,112)
WeakShort      4,112         —      MyApp.Models.User (3,881), System.Action (231)
WeakLong         441         —      System.String (441)
Pinned         4,882      910 MB    System.Byte[] (4,441), MyApp.Interop.Buffer (441)
AsyncPinned    1,112      120 MB    System.Byte[] (1,112)
Dependent         12       <1 MB    System.Object (12)
```
