# timer-leaks

**Category:** Infrastructure / Network  
**Included in `analyze --full`:** Yes

## What it does

Finds all live `System.Threading.Timer` instances on the heap and shows their callback method, due time, period, and size. Timers are GC roots — they prevent their callback target from being collected until the timer is disposed. This command surfaces timer accumulation that is otherwise invisible.

---

## Analyzer: `TimerLeaksAnalyzer`

**Implements:** `IHeapObjectConsumer`  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

`HeapTypeMeta.IsTimer` is set for `System.Threading.Timer`, `System.Threading.TimerQueueTimer`, and other types in the `System.Threading` namespace whose name contains `Timer`.

### How it works

For each object where `meta.IsTimer` is true, the analyzer reads `_dueTime` and `_period` by trying `ReadField<long>` first and falling back to `ReadField<int>`, returning `-1` on any failure.

Callback resolution requires `ClrRuntime`: the `m_callback` delegate field is read, then `_methodPtr` (a native code pointer) is extracted from it. `runtime.GetMethodByInstructionPointer(ptr)` resolves this to an `IClrMethod`, from which the callback string is formatted as `"{TypeName}.{MethodName}"` and the module filename is extracted. If any step fails, the callback falls back to the delegate type name or empty string.

In the pre-warm path, `ctx.Runtime` is passed to the analyzer so callback resolution runs identically to standalone mode. Clone/merge: each parallel clone receives the same `_runtime` reference; `MergeFrom` appends item lists.

---

## Pre-warm path

`TimerLeaksAnalyzer` itself is the consumer in `DumpCollector.CollectHeapObjectsCombined` — the full detail analyzer (callback resolution, `_dueTime`, `_period` reads) runs directly during the combined walk. Populates `TimerLeaksData` in the session cache. In the pre-warm pass `ctx.Runtime` is available so `IClrMethod` resolution is performed the same way as in standalone mode.

---

## Consumer: `LightweightStatsConsumer` (count only)

**Class**: `DumpDetective.Analysis.Memory.Consumers.LightweightStatsConsumer`  
**Walk**: `HeapObjectCollector.CollectHeapObjects` — lightweight, no-context heap scan  
**Purpose**: Increments `TimerCount` for `DumpSnapshot.TimerCount` used in health scoring. No callback resolution or `_dueTime`/`_period` reads — those are performed only by the full `TimerLeaksAnalyzer`.

---

## Disk cache

None.

---

## Options

| Option | Description |
|---|---|
| `--min-count <n>` | Suppress output if total timer count < n (default: show all) |
| `--filter <text>` | Substring match on `Callback` or `Module` |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **Hundreds or thousands of timers** | `new Timer(...)` called in a loop or per-request without `using` — classic timer leak. Each `Timer` is a GC root keeping its callback target alive. |
| **Periodic timer with `PeriodMs < 100`** | High-frequency polling — consider `Task`-based polling with `await Task.Delay` instead, which doesn't hold a GC root continuously. |
| **Many one-shot timers (`PeriodMs = -1`) with large `DueMs`** | Timeout callbacks from requests that already completed but whose timers were never cancelled/disposed. |
| **`DueMs = -1`** | Timer is disabled (created with `Timeout.Infinite`). If many of these accumulate, they are forgotten timers that should have been disposed. |
| **Callback resolves to a type that implements `IDisposable`** | The timer is keeping that object alive past its expected disposal. Check the object lifecycle. |
| **`Module` is empty** | Callback is a dynamic method or lambda in an anonymous class — still a real timer, but harder to trace back to source. |

---

## Typical output shape

```
Timer Leaks — 4,831 live timers

Callback Method                              Module              Due Time   Period    Count    Total Size
MyApp.RequestTracker.OnTimeout               MyApp.dll           30,000 ms   −1 ms    4,712    18.4 MB
System.Net.Http.HttpClientHandler.Cleanup    System.Net.Http     60,000 ms   −1 ms       92   360 KB
MyApp.Cache.EvictionTimer                    MyApp.dll            5,000 ms  5,000 ms     27   108 KB
<unknown>                                    —                    −1 ms      −1 ms         0   <1 KB
```
