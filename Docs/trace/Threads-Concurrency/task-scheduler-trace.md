# task-scheduler-trace

**Category:** Trace — Threads and Async  
**Included in `trace-analyze`:** Yes

## What it does

Analyzes `Task/Scheduled`, `Task/Started`, `Task/Completed`, and `Task/Failed` events to detect long-running tasks, tasks with excessive wait depth, and cancelled task storms. Identifies which continuations are occupying the thread pool for longest and which task chains have the deepest nesting.

---

## Analyzer: `TaskSchedulerTraceAnalyzer`

**Namespace:** `DumpDetective.Analysis.Trace.Analyzers`

Matches `Task/Scheduled` → `Task/Started` → `Task/Completed` triplets by task ID. Computes queuing latency (scheduled → started), execution time (started → completed), and total lifetime. Tasks with execution times above the 99th percentile are flagged. Cancelled tasks are counted separately. Continuation depth is measured by tracking `parentTaskId` chains up to a configurable maximum depth.

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `--top <n>` | Top long-running tasks to show (default: `20`) |
| `--depth <n>` | Maximum continuation chain depth to report (default: `10`) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## What to look for

| Signal | What it means |
|---|---|
| High queuing latency | ThreadPool is saturated; new tasks wait before starting |
| Long execution time + long continuation chain | Synchronous blocking inside async code |
| High cancelled task rate | Timeout or CancellationToken pressure; check timeout values |
| Tasks with depth > 10 | Deep async chains can cause stack overflows in continuation scenarios |
