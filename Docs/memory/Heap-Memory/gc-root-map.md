# gc-root-map

**Category:** GC / Lifetime  
**Included in `analyze --full`:** Yes

## What it does

Enumerates all GC handles and thread stack roots, then groups them by root kind and shows which object types are most frequently held per category. This gives a structural overview of what is keeping the heap alive: how much is held by strong handles vs. pinned handles vs. thread stacks vs. static fields.

Useful when `memory-leak` identifies a type as a suspect but the root chain is not obvious — `gc-root-map` shows which root *category* is most likely holding it.

---

## Analyzer: `GcRootMapAnalyzer`

**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

Calls `heap.EnumerateRoots()` and buckets each root by `ClrRootKind`:

| Kind | Description |
|---|---|
| `Strong` | `GCHandle.Alloc` without pinning — long-lived objects |
| `Pinned` | `GCHandle.Alloc(Pinned)` — prevents GC movement; blocks compaction |
| `AsyncPinned` | I/O completion buffers pinned by async operations |
| `WeakShort` | Collected at any GC |
| `WeakLong` | Collected after resurrection check |
| `RefCounted` | COM interop reference-counted handles |
| `SizedRef` | AppDomain-scoped size-tracking handles (rare) |
| `Dependent` | `DependentHandle` — keeps target alive only while primary is alive |
| `Stack` | Thread stack roots (local variables and arguments on active frames) |
| `Finalizer` | Objects in the finalization queue |

For each kind, the top object types by instance count and total size are reported.

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
| High `Pinned` count with large types | Buffer pinning is preventing LOH compaction |
| Many `AsyncPinned` handles | Concurrent async I/O is pinning many buffers simultaneously |
| `Strong` handles holding large graphs | `GCHandle` used as a cache without eviction |
| Unexpected types on `Stack` | Live-object investigation starting from a specific thread |
