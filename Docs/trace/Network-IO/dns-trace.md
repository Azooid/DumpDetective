# dns-trace

**Category:** Trace — Network and I/O  
**Included in `trace-analyze`:** Yes

## What it does

Measures DNS lookup latency, failure storms, and top hostnames from `System.Net.NameResolution` events. DNS is a common hidden latency source in cloud services: when DNS resolution is not cached aggressively, repeated lookups add milliseconds to every outbound request.

---

## Analyzer: `DnsTraceAnalyzer`

**Namespace:** `DumpDetective.Analysis.Trace.Analyzers`

Parses `ResolutionStart` / `ResolutionStop` event pairs keyed by activity ID. For each resolution: hostname, resolved addresses, latency, and failure reason (if any) are recorded. Failure storms are detected when the same hostname fails more than 5 times within 10 seconds. Results are grouped by hostname, sorted by average latency descending.

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `--top <n>` | Top hostnames to show (default: `20`) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## What to look for

| Signal | What it means |
|---|---|
| Same hostname resolved hundreds of times | Missing `HttpClient` reuse or DNS TTL not cached |
| Failure storm on one hostname | DNS outage or misconfigured search domain |
| P99 latency > 50 ms | DNS server overloaded or cross-datacenter resolution |
