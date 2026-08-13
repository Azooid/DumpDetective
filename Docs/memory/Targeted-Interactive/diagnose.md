# diagnose

**Category:** Orchestration  
**Included in `analyze --full`:** No (runs multiple analyzers on demand)

## What it does

Synthesizes findings from multiple analyzers into a prioritized diagnostic summary with two sections:

- **Executive summary** — one paragraph describing the dominant problem category and confidence level, written for non-engineers.
- **Engineering summary** — ranked list of contributing factors with evidence (thread counts, type names, sizes), causal chain reasoning, and specific remediation steps.

This command is designed to be the first thing an engineer reads after capturing a dump, answering: "What is most likely wrong and what should I check next?"

---

## Analyzers used

| Analyzer | Evidence contributed |
|---|---|
| `ConfigurationSmellAnalyzer` | Known-bad runtime configuration patterns |
| `WorkloadProfileClassifier` | I/O-bound vs. CPU-bound vs. mixed workload classification |
| `ExceptionAnalysisAnalyzer` | Live exception type distribution |
| `MemoryLeakAnalyzer` | Top leak suspects by retained size |
| `ThreadAnalysisAnalyzer` | Blocked thread count, wait kinds, GC mode |
| `AsyncStacksAnalyzer` | Async state machine backlog and suspension states |
| `DeadlockAnalyzer` | Deadlock cycle detection |

Each analyzer runs independently. Results are scored and blended into the narrative.

---

## Options

| Option | Description |
|---|---|
| `<dump>` | Path to `.dmp` or `.mdmp` file |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |
| `-h, --help` | Show this help |

---

## When to use vs. `analyze --full`

| | `analyze --full` | `diagnose` |
|---|---|---|
| Runs all 33 analysis commands | ✓ | ✗ |
| Produces individual sub-reports | ✓ | ✗ |
| Produces narrative summary | ✗ | ✓ |
| Run time | ~2–5 min (large dumps) | ~30–90 s |
| Best for | Deep investigation | First triage step |
