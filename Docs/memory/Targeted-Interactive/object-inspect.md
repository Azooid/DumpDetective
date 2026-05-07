# object-inspect

**Category:** Targeted / Interactive  
**Included in `analyze --full`:** No (requires `--address` argument)

## What it does

Deep-inspects a specific managed object by heap address. Shows type, generation, finalizer/pinned status, and a recursive breakdown of all field values — including nested objects up to a configurable depth. Optionally computes exclusive retained size per reference field via BFS.

---

## Analyzer: `ObjectInspectAnalyzer`

**Class type:** Not `IHeapObjectConsumer` — single-object targeted inspection  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

### How it works

Resolves the object at the given address via `ctx.Heap.GetObject(address)`. If the address is invalid (free space or outside all segments), reports a warning and returns.

Two annotation sets are built upfront: `ctx.Heap.EnumerateFinalizableObjects()` produces a `HashSet<ulong>` of addresses in the finalizer queue; `ctx.Runtime.EnumerateHandles()` filtered by `h.IsPinned` produces a set of pinned addresses. Both are used to annotate the root object and any nested reference objects encountered during the recursive walk.

The recursive field walk (`InspectFields`) iterates `obj.Type.Fields`, skipping static fields. Field dispatch is based on `field.ElementType`: string fields are read via `field.ReadObject(address, interior: false).AsString(256)` with a 256-character cap; primitive value-type fields are read with the appropriate `Read<T>` call; reference fields and arrays trigger optional recursion. Recursion stops when the depth limit (`--depth`, default 5) is reached or when the object address is already in the `visited` `HashSet<ulong>` (cycle guard).

For array fields, the element type and length are read, and up to `--max-array` elements are sampled. For reference fields with `--retained`, retained sizes are looked up via `BfsIndexCache.GetRetainedSize(addr)` (O(1) if cache loaded) or computed by BFS from scratch.

---

## Disk cache

Uses `BfsIndexCache` (`.ddcache/<dumpName>/<dumpName>.bfs.idx`) for retained-size computation if present. Run `build-bfs` before heavy use of `--retained` to avoid per-field BFS from scratch.

---

## Options

| Option | Description |
|---|---|
| `--address, -x <hex>` | Object address in hexadecimal (required) |
| `-d, --depth <n>` | Recursive descent depth (default: 5) |
| `--max-array <n>` | Max array elements to display (default: 10) |
| `--retained, -r` | Compute retained size per reference field via BFS |
| `--retained-cap <n>` | BFS node cap per field walk (default: unlimited) |
| `--no-cache` | Force BFS rebuild even if cache exists |
| `--no-save` | Do not save BFS index after building |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **Unexpected field values** | Misconfigured timeouts, stale state captured in a closure or async state machine, wrong environment constants. |
| **Large retained size on one reference field** | That sub-graph dominates this object's memory cost — the leak is rooted there. Identify the field and trace back to the root. |
| **Object in finalizer queue** | Waiting to be finalized. Check if `Dispose()` was supposed to prevent this. |
| **Object is pinned** | A `GCHandle.Pinned` is holding it. Check `pinned-objects` for context on all pinned objects. |
| **Reference field → null unexpectedly** | Use-after-dispose, incomplete initialization, or late assignment. |
| **Array field with huge `Length` but few active elements** | Over-allocated backing array. The array itself is the memory cost — capacity not trimmed after usage. |
| **String fields containing sensitive data** | Credentials, connection strings, tokens in memory — ensure sensitive data is zeroed after use. |

---

## Typical output shape

```
Object Inspector — MyApp.Models.Order @ 0xFFFF1234

Type:       MyApp.Models.Order
Size:       8.4 MB  (own)
Generation: Gen2
Finalizer:  No
Pinned:     No

Fields:
  [string]   _orderId           "ORD-2024-991182"
  [int]      _status            3
  [string]   _customerName      "Acme Corp"
  [ref]      _lines             MyApp.Collections.OrderLineList @ 0xFFFE4421
               Retained: 8.1 MB
               [int]  _count    12,441
               [ref]  _items    System.Object[] @ 0xFFFC1100  (length: 16,384)
  [ref]      _metadata          MyApp.Models.OrderMetadata @ 0xFFFB2200
               Retained: 0.3 MB
```
