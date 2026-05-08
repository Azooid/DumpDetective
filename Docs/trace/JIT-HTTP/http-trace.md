# http-trace

**Category:** Trace — JIT / HTTP  
**Included in `trace-analyze`:** Yes

## What it does

Parses HTTP request lifecycle events from a `.nettrace` or `.etl` trace file to measure request latency and identify slow or failing endpoints. Reports:

- Total request count, average latency, P99 latency, error count and rate
- **Top endpoints by request count** — with avg/max latency and error count per path
- **Slowest individual requests** — ordered by duration, above `--slow-ms` threshold
- A stacked bar chart of average latency by endpoint (top 8)
- Alerts for P99 > 2 s and error rate > 5%

---

## Events consumed

| Provider | Stack | Purpose |
|---|---|---|
| `Microsoft-AspNetCore-Hosting` | ASP.NET Core (`Kestrel` / in-process) | `Request/Start`, `Request/Stop` pairs with path and status code |
| `Microsoft-Windows-ASPNET` | IIS / classic ASP.NET | Equivalent request lifecycle events |

Start and stop events are matched by request ID. Duration is the wall-clock time between `Request/Start` and `Request/Stop`.

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `-n, --top <n>` | Top N endpoints and slowest requests (default: 20) |
| `--process <name>` | Filter to a specific process name |
| `--slow-ms <ms>` | Requests above this threshold are listed as "slow" (default: 1000 ms) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## Usage

```bash
DumpDetective http-trace perf.etl --process w3wp
DumpDetective http-trace app.nettrace --slow-ms 500 --top 30
DumpDetective http-trace perf.etl --process w3wp --output http-report.html
```

---

## Collecting compatible traces

```bash
# dotnet-trace — ASP.NET Core
dotnet trace collect --providers 'Microsoft-AspNetCore-Hosting:0xFFFF:5' -p <pid>
# or
dotnet trace collect --profile asp.net -p <pid>

# dotnet-trace — IIS / ASPX
dotnet trace collect --providers 'Microsoft-Windows-ASPNET:0xFFFF:5' -p <pid>

# PerfView — includes HTTP via network capture
PerfView.exe /ClrEvents:Default /NetworkCapture /NoGui collect

# PerfView — focus process (replace 3392 with actual PID)
PerfView.exe /ClrEvents:Default /NetworkCapture /FocusProcess:"3392" /NoGui collect
```

---

## Interpreting results

| Signal | Likely cause | Action |
|---|---|---|
| P99 > 2 s | Lock contention, GC pauses, slow DB queries, thread pool starvation | Correlate with `contention-trace`, `gc-trace`, and `threadpool-starvation` chapters |
| Error rate > 5% | Upstream dependency failures, exception-as-control-flow, timeouts | Correlate with `exceptions-trace`; check `500` vs `4xx` split |
| One endpoint dominates latency | N+1 query, missing index, large payload serialisation | Check `alloc-trace` for serialisation types and `cpu-trace` for the call tree of that path |
| High request count + short avg latency | Normal traffic pattern | No action needed |
| Missing HTTP data | Trace collected without HTTP provider | Re-collect (see below) |

---

## No data?

If no HTTP events appear, the trace was collected without the ASP.NET Core hosting provider. Re-collect with:

```bash
# dotnet-trace (ASP.NET Core)
dotnet trace collect --profile asp.net -p <pid>
dotnet trace collect --providers 'Microsoft-AspNetCore-Hosting:0xFFFF:5' -p <pid>

# dotnet-trace (IIS/ASPX)
dotnet trace collect --providers 'Microsoft-Windows-ASPNET:0xFFFF:5' -p <pid>

# PerfView
PerfView.exe /ClrEvents:Default /NetworkCapture /NoGui collect
```
