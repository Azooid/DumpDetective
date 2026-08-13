# process-lifecycle-trace

**Category:** Trace — Observability  
**Included in `trace-analyze`:** Yes

## What it does

Detects process crashes, restarts, and abnormal exits from Process/Start/Stop events. Useful in container or IIS environments where processes are recycled automatically — the trace may contain evidence of a crash that happened before the process was restarted.

---

## Analyzer: `ProcessLifecycleAnalyzer`

**Namespace:** `DumpDetective.Analysis.Trace.Analyzers`

Parses `Process/Start` and `Process/Stop` event pairs for all processes in the trace. For each process: name, PID, start time, exit time, and exit code are recorded. Non-zero exit codes are flagged as abnormal. Process duration is computed. Processes with duration below 5 seconds are flagged as crash-loop candidates.

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## What to look for

| Signal | What it means |
|---|---|
| Exit code `0xC0000005` (access violation) | Native crash or stack corruption |
| Exit code `1` or `-1` with short duration | Unhandled exception at startup |
| Same process name restarting every few seconds | Crash loop — check `exceptions-trace` from the same window |
| Worker process exit during trace | IIS/ASP.NET process recycle; correlate with request errors |
