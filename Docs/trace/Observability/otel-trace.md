# otel-trace

**Category:** Trace — Observability  
**Included in `trace-analyze`:** Yes

## What it does

Measures OpenTelemetry Activity span latency, error rates, and top operations from `DiagnosticSource` events. Provides a span-level view of distributed tracing data that was collected in-process, without requiring an external tracing backend.

---

## Analyzer: `OpenTelemetryTraceAnalyzer`

**Namespace:** `DumpDetective.Analysis.Trace.Analyzers`

Parses `Activity.Start` and `Activity.Stop` events from `Microsoft-Diagnostics-DiagnosticSource`. Matches start/stop pairs by activity ID. For each span: operation name, kind (`Client`, `Server`, `Producer`, `Consumer`, `Internal`), latency, status code, and error flag are recorded. Results are grouped by operation name, sorted by average latency and error rate descending.

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `--top <n>` | Top operations to show (default: `20`) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## What to look for

| Signal | What it means |
|---|---|
| High error rate on one operation | Downstream service failure or misconfiguration |
| P99 >> P50 on a specific operation | Outliers — check for timeouts or retries |
| `Client` span latency >> expected | External call is slow; correlate with `http-trace` or `sql-trace` |
| Missing spans for expected operations | Instrumentation gap or sampling dropping events |
