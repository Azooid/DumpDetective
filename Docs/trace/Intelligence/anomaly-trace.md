# anomaly-trace

**Category:** Trace — Intelligence  
**Included in `trace-analyze`:** Yes

## What it does

Performs statistical anomaly detection using z-score analysis over CPU, GC, allocation, contention, and exception rate timelines. Rather than looking at a single metric, it correlates anomalous windows across all five dimensions to identify the most likely causal interval.

---

## Analyzer: `AnomalyDetectionAnalyzer`

**Namespace:** `DumpDetective.Analysis.Trace.Analyzers`

Computes per-second buckets for five metrics from the other trace analyzers' raw data:

| Metric | Source |
|---|---|
| CPU sample rate | `CpuTraceAnalyzer` |
| GC pause ms | `GcTraceAnalyzer` |
| Allocation bytes | `AllocationBurstAnalyzer` |
| Contention wait ms | `ContentionTraceAnalyzer` |
| Exception count | `ExceptionsTraceAnalyzer` |

For each metric, a z-score is computed per time bucket: `(value − mean) / stddev`. Buckets with z-score > 2.5 in any metric are flagged as anomalous. Buckets with z-score > 2.5 in two or more metrics are flagged as correlated anomalies and ranked by the sum of z-scores.

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `--threshold <z>` | Z-score threshold for anomaly flagging (default: `2.5`) |
| `--top <n>` | Top anomaly windows to show (default: `10`) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## What to look for

| Signal | What it means |
|---|---|
| CPU + allocation z-score spike together | Hot allocation path saturating CPU and triggering GC |
| GC + contention z-score together | GC stop-the-world causing lock convoy as threads resume |
| Isolated exception z-score spike | Transient downstream failure — not correlated with CPU/GC |
| All metrics elevated simultaneously | System-wide saturation; check process-level resource limits |
