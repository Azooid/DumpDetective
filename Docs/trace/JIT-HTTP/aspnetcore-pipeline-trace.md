# aspnetcore-pipeline-trace

**Category:** Trace — HTTP and ASP.NET  
**Included in `trace-analyze`:** Yes

## What it does

Analyzes `Microsoft.AspNetCore.Hosting` and `Microsoft.AspNetCore.Routing` events to detect auth failures, unmatched routes, and endpoint error patterns. Provides a per-endpoint breakdown of request counts, error rates, and latency distribution.

---

## Analyzer: `AspNetCorePipelineAnalyzer`

**Namespace:** `DumpDetective.Analysis.Trace.Analyzers`

Parses `RequestStart` / `RequestStop` pairs keyed by activity ID. Reads: HTTP method, path, status code, and unhandled exception type (if any). Groups by route template (from routing events) or normalized path. Auth failures are identified by `401` / `403` status codes or `AuthenticationFailed` events. Unmatched routes appear as `404` with no matching endpoint event.

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `--top <n>` | Top endpoints to show (default: `20`) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## What to look for

| Signal | What it means |
|---|---|
| High 401/403 rate on one endpoint | Token expiry or misconfigured auth middleware |
| 404 spike with consistent path prefix | Client routing bug or broken link |
| P99 latency >> P50 on one endpoint | Outlier requests; check for database timeouts or GC pauses |
| Unhandled exception on specific route | Missing error handling or null dereference in controller |
