# gc-roots

**Category:** Heap / Memory  
**Included in `analyze --full`:** No (requires `--type` or `--address` argument)

## What it does

Traces back from instances of a specific type (or a specific object address) to the GC roots — static fields, thread stack slots, and GC handles — that are keeping those objects alive. This answers the hardest diagnostic question: "why has this object not been collected?"

Each target object is reported with its direct roots (handles, stack roots, static fields that reference it directly) and optionally with its indirect referrers (other heap objects that hold a reference to it). The indirect referrer scan is the most expensive part and is why this command is excluded from `analyze --full`.

---

## Analyzer: `GcRootsAnalyzer`

**Class type:** Not `IHeapObjectConsumer` — targeted search, not a bulk walk  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

### How it works

**Finding targets**: if `--address` is given, the single object is resolved directly via `ctx.Heap.GetObject(addr)`. No scan needed. If `--type` is given, `heap.EnumerateObjects()` runs with a case-insensitive substring match on each object's type name. The scan is capped at `--max` results (default: 10) to avoid running for minutes on types with millions of instances.

**Direct root scan**: for each target object, the analyzer checks three root categories:
- **GC handles**: iterates `ctx.Runtime.EnumerateHandles()` and matches each handle's object address against the target set. Reports the handle kind (`Strong GCHandle`, `Pinned GCHandle`, etc.).
- **Thread stack roots**: for each managed thread, iterates `thread.EnumerateStackRoots()` and matches stack slot addresses. Reports the managed thread ID with each stack root.
- **Static fields**: iterates all non-system modules, types, and static reference fields, reading each field value with `f.ReadObject(AppDomain[0])` and matching against the target set. Reports the declaring type and field name.

**Indirect referrer scan** (skipped with `--no-indirect`): walks all live heap objects and for each one checks whether any of its reference fields point at a target address. This is O(total objects × average fields per object) and is the expensive part — it can take 30+ seconds on large dumps. The result is a list of `(referrerAddr, referrerType)` pairs per target.

This command does not use the `BfsIndexCache` or `SharedReferrerCache` because it needs to search for a specific set of addresses rather than compute retained sizes.

---

## Disk cache

None.

---

## Options

| Option | Description |
|---|---|
| `--type <name>` | Type name substring (case-insensitive). Required unless `--address` is used. |
| `--address <hex>` | Specific heap address (e.g. `0xFFFF1234ABCD`) |
| `--max <n>` | Maximum number of matching objects to analyze (default: 10) |
| `--no-indirect` | Skip the referrer scan — show only direct GC roots (much faster) |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **Static field root** | Object kept alive by a global/singleton field. Will never be collected while the AppDomain runs. Run `static-refs` to see all static roots with retained sizes. |
| **Stack root (Thread N)** | Object in an active method call on that thread. Expected for objects in use; unexpected if the thread is stuck or abandoned. |
| **Strong GCHandle root** | Explicit `GCHandle.Alloc(Normal)` — COM interop, explicit object persistence. Ensure the handle is freed when done. |
| **Finalizer queue root** | Object waiting for finalization and also strongly rooted — resurrection pattern. |
| **No direct roots found** | Object is unreachable (will be collected next GC) or the dump was taken after GC ran. |
| **Indirect referrer chain longer than 5 hops** | Deep object graph. The root is far from the target — use `memory-leak` to understand the whole tree. |

---

## Typical output shape

```
GC Roots — MyApp.Models.Order  •  3 instances  •  capped: No

Object 0xFFFF1234  MyApp.Models.Order  1.2 MB  Gen2
  Direct roots:
    Static: MyApp.Services.OrderCache._activeOrders (Dictionary`2)
    Handle: Strong GCHandle → MyApp.Infrastructure.Session

Object 0xFFFE4421  MyApp.Models.Order  880 KB  Gen2
  Direct roots:
    Stack: Thread 8812  →  MyApp.Controllers.OrderController.ProcessAsync()
  Referrers (indirect):
    MyApp.Models.OrderLine @ 0xFFFD1100  (_order field)
    MyApp.Models.OrderLine @ 0xFFFD2200  (_order field)
    ...and 2,879 more
```
