# context-switch-trace

**Category:** Trace — Threads & Concurrency  
**Included in `trace-analyze`:** Yes (produces no data if trace has no CSwitch events)

## What it does

Analyzes Windows kernel **CSwitch** (context switch) events from an `.etl` trace to reveal:

- **Voluntary vs preempted split** — voluntary switches mean a thread blocked itself (waiting for I/O, a lock, a timer, or an async continuation); preempted switches mean the OS scheduler took the CPU away because the thread's time quantum expired or a higher-priority thread became runnable.
- **Wait-reason distribution** — what threads block on most frequently: lock/mutex contention (`WrMutex`, `WrResource`), thread-pool queue (`WrQueue`), timers (`WrTimer`), page faults (`WrPageIn`), COM/RPC (`WrLpcReceive`), and more.
- **Top threads by switch count** — identifies spinning or chatty threads consuming excessive scheduler attention.
- **Average CPU run-slice** — how long threads hold the CPU between context switches; very short slices indicate spinning or excessive lock bouncing.
- **CPU idle time** — how much wall time was the CPU idle; low idle time indicates full CPU saturation.

> **Kernel events only.** CSwitch events are not captured by `dotnet-trace` (`.nettrace`). You must use **PerfView** or **xperf** to collect an `.etl` trace with kernel events. Running against a `.nettrace` file produces an empty report with no data.

---

## Collecting a trace

### PerfView (recommended)

```bash
# Kernel CSwitch + CLR events
PerfView.exe /KernelEvents:ContextSwitch /ClrEvents:Default /NoGui collect

# Full kernel events (includes CSwitch + CPU sampling — preferred for trace-analyze)
PerfView.exe /KernelEvents:Default /ClrEvents:Default /NoGui collect
```

### xperf / WPR

```bash
xperf -on PROC_THREAD+LOADER+CSWITCH -f kernel.etl
# ... (let app run under load) ...
xperf -stop
xperf -merge kernel.etl merged.etl
```

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.etl` file (must contain CSwitch kernel events) |
| `--top <N>` | Top N threads to show (default: 30) |
| `--process <name>` | Filter to a specific process name |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## Report sections

| Section | Content |
|---|---|
| **Summary** | Total switches, voluntary %, preempted %, avg run-slice, CPU idle time |
| **Wait-reason breakdown** | Distribution of why threads blocked (WrQueue, WrMutex, WrTimer, etc.) |
| **Top threads by switch count** | Per-thread: switch count, voluntary %, preempted %, avg run-slice ms |

---

## What to look for

### Good indicators

| Signal | Meaning |
|---|---|
| Voluntary % > 70% | Threads are politely yielding — healthy async/await or I/O-bound workload |
| Preempted % < 20% | CPU not saturated; scheduler rarely needs to forcibly preempt threads |
| Avg run-slice 5–50 ms | Threads hold CPU for a reasonable quantum before switching |
| CPU idle > 30% | Headroom exists; workload is not CPU-bound |
| `WrQueue` dominant | Threads mostly waiting for thread-pool work items — normal for async workloads |

### Bad indicators

| Signal | Meaning |
|---|---|
| Preempted % > 40% | CPU saturation — more runnable threads than cores; add more capacity or reduce work |
| Preempted % 20–40% | CPU pressure — investigate with `cpu-trace` for hot methods |
| Avg run-slice < 1 ms | Spinning — threads acquire/release locks or check conditions in a tight loop |
| `WrResource` / `WrMutex` > 30% | Lock contention bottleneck — investigate with `contention-trace` |
| `WrPageIn` > 10% | Excessive page faults — working set larger than available RAM |
| `WrLpcReceive` > 20% | Threads blocked on COM/RPC calls — may indicate cross-apartment or out-of-process marshalling overhead |
| CPU idle < 10% | Full CPU saturation — system has no spare cycles |

### Three-step action guide

1. **High preempted % + low idle** → CPU-bound; use `cpu-trace` to find hot methods and reduce algorithmic cost or add capacity.
2. **High voluntary % + `WrResource`/`WrMutex`** → lock contention; use `contention-trace` to find contested call sites and switch to lock-free structures or `SemaphoreSlim`.
3. **Short avg run-slice + `WrQueue` dominant** → thread-pool thrashing; use `thread-pool-starvation` and `async-trace` to find sync-over-async patterns or excessive `Task.Run` usage.
