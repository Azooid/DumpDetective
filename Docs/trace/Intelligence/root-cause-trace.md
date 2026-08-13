# root-cause-trace

**Category:** Trace — Intelligence  
**Included in `trace-analyze`:** Yes

## What it does

Runs all 29 trace sub-analyzers and synthesizes their findings into ranked causal chains with actionable remediation advice. Each chain is a sequence of cause → effect relationships inferred from correlations across multiple analyzer outputs.

This is the highest-signal single command for trace analysis: instead of reading 29 separate reports, you get a prioritized list of root causes with evidence and remediation steps.

---

## Analyzer: `RootCauseChainAnalyzer`

**Namespace:** `DumpDetective.Analysis.Trace.Analyzers`

After all 29 sub-analyzers complete, `RootCauseChainAnalyzer.Analyze` receives their combined data. It applies a rule set of ~40 causal chain patterns, each defined as:

- **Trigger condition**: a threshold on one or more analyzer outputs (e.g., GC pause > 100 ms AND allocation burst factor > 3×)
- **Causal inference**: a text description of the likely causal mechanism
- **Evidence references**: which analyzer data support the inference
- **Confidence score**: 0–100 based on how many conditions in the pattern are met
- **Remediation steps**: ordered list of actionable recommendations

Chains are ranked by confidence score descending. The top 10 are shown by default.

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `--top <n>` | Top causal chains to show (default: `10`) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## When to use

Use `root-cause-trace` as a starting point when you do not yet know what category of problem you are looking for. Use individual analyzer commands (e.g., `cpu-trace`, `gc-trace`) when you already have a hypothesis and want lower-level evidence.

For cross-source analysis (trace + dump together), use `trace-dump-analyze` instead.
