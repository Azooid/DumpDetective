# native-interop

**Category:** Threads / Concurrency  
**Included in `analyze --full`:** Yes

## What it does

Identifies threads currently blocked in native or runtime transition frames: P/Invoke calls, CLR helper stubs, GC coordination, and interop transitions. Reports the top native call sites shared across threads and the full mixed managed+native stack for each affected thread.

Native-blocked threads do not appear as "waiting" in GC mode registers and are often missed by `thread-analysis`. This command surfaces them explicitly.

---

## Analyzer: `NativeInteropAnalyzer`

**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

Iterates all threads via `ClrRuntime.Threads`. For each thread, walks the stack frames using `ClrThread.EnumerateStackTrace`. A frame is classified as native/interop when:

- `ClrStackFrame.Kind == Native`
- The method name contains `PInvoke`, `InternalCall`, `JIT_`, `CLR_`, `clr!`, or `ntdll`
- The frame module is not a managed assembly

Threads with at least one such frame are grouped by their top native symbol. The report shows the count of threads sharing each native call site, then individual thread stacks with managed frames interleaved.

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
| Many threads in the same `ntdll!NtWaitForSingleObject` | Classic monitor wait or OS sync primitive |
| Threads in GC coordination frames | GC is running long or is blocked; check `gen-summary` |
| P/Invoke in tight loop (many threads same site) | Unmanaged API bottleneck |
| Threads stuck in COM interop | `[ComImport]` call or RCW/CCW lifetime issue |
