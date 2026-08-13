# retry-storm-trace

**Category:** Trace — Observability  
**Included in `trace-analyze`:** Yes

## What it does

Identifies bursts of transient retry-pattern exceptions — `TimeoutException`, `SocketException`, `SqlException`, `HttpRequestException`, and similar — that indicate a retry storm. A retry storm occurs when a downstream service becomes slow or unavailable, all callers retry simultaneously, and the combined retry load prevents recovery.

---

## Analyzer: `RetryStormAnalyzer`

**Namespace:** `DumpDetective.Analysis.Trace.Analyzers`

Parses first-chance exception events for known transient exception types. Groups exceptions into 1-second buckets. A retry storm window is flagged when the exception rate exceeds 5× the baseline for more than 3 consecutive seconds. The report shows storm start time, duration, peak exception rate, and the dominant exception type with throw site.

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `--factor <n>` | Storm factor threshold (default: `5.0`) |
| `--duration <s>` | Minimum storm duration in seconds (default: `3`) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## What to look for

| Signal | What it means |
|---|---|
| Exception burst with `SocketException` | Network outage or DNS failure; retry logic amplifying load |
| `SqlException` burst correlating with connection pool exhaustion | DB saturation — retries are making the problem worse |
| Burst subsides without code change | Downstream service recovered; retry amplification is self-resolving |
| Burst doesn't subside | Infinite retry loop without backoff; add exponential backoff + jitter |
