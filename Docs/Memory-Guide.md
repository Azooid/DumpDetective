# DumpDetective Memory Guide

Complete standalone reference for memory-dump workflows and commands. This guide is intended to be sufficient without opening CLI help.

## Scope

Input file types:

- `.dmp`
- `.mdmp`

Primary use cases:

- Memory growth and retention
- GC pressure and fragmentation
- Finalizer/handle/backlog diagnostics
- Thread and deadlock inspection from a dump snapshot
- Cross-dump trend analysis and report replay/diff

## Fast Start

```bash
# Full incident report
DumpDetective analyze app.dmp --full

# Save replayable output (recommended)
DumpDetective analyze app.dmp --full --output report.bin

# Re-render without reopening dump
DumpDetective render report.bin --output report.html
```

## Output And Replay Model

- Most memory commands emit HTML by default.
- Use `.bin` when you want compact archival and later replay.
- `render` converts saved `.json`/`.bin` into `.html`, `.md`, `.txt`, `.json`, or `.bin`.
- `diff` compares two saved reports (`.json`/`.bin`) without reopening dumps.

## Command Index

Included in `analyze --full` unless marked No.

| Command | Included in `--full` |
|---|:---:|
| `heap-stats` | Yes |
| `gen-summary` | Yes |
| `heap-fragmentation` | Yes |
| `large-objects` | Yes |
| `pinned-objects` | Yes |
| `memory-leak` | Yes |
| `high-refs` | Yes |
| `string-duplicates` | Yes |
| `finalizer-queue` | Yes |
| `handle-table` | Yes |
| `static-refs` | Yes |
| `weak-refs` | Yes |
| `thread-analysis` | Yes |
| `thread-pool` | Yes |
| `deadlock-detection` | Yes |
| `async-stacks` | Yes |
| `exception-analysis` | Yes |
| `event-analysis` | Yes |
| `http-requests` | Yes |
| `connection-pool` | Yes |
| `wcf-channels` | Yes |
| `timer-leaks` | Yes |
| `module-list` | Yes |
| `gc-roots` | No |
| `type-instances` | No |
| `object-inspect` | No |
| `build-bfs` | No |

## Workflow Commands

### `analyze`

Usage:

```text
DumpDetective analyze <dump-file> [options]
```

What it does:

- Produces a scored health report for one dump.
- `--full` adds all embedded sub-reports.

Options:

- `--full` Full combined report.
- `--str-top <n>` `string-duplicates` max groups (default `100`).
- `--str-min-count <n>` `string-duplicates` min duplicate count (default `2`).
- `--str-min-waste <bytes>` `string-duplicates` min wasted bytes (default `0`).
- `--bfs-depth <n>` `static-refs` BFS sample depth (default is about 1% of heap objects).
- `--exact` `static-refs` full BFS (slower, precise).
- `-o, --output <file>` Output file.
- `-h, --help` Help.

Examples:

```bash
DumpDetective analyze app.dmp
DumpDetective analyze app.dmp --full
DumpDetective analyze app.dmp --full --output full-report.html
DumpDetective analyze app.dmp --full --output full-report.html --format bin
```

### `trend-analysis`

Usage:

```text
DumpDetective trend-analysis <dump1> <dump2> [<dump3> ...] [options]
DumpDetective trend-analysis <dump-directory> [options]
DumpDetective trend-analysis --list <paths.txt> [options]
```

Options:

- `--list <file>` Load dump paths from text file.
- `--full` Full collection per dump with richer data and embedded sub-reports.
- `--baseline <n>` 1-based baseline dump index (default `1`).
- `--ignore-event <type>` Exclude matching event publisher types (repeatable).
- `--prefix <p>` Dump label prefix (default `D`).
- `--str-top <n>` String duplicate groups max (default `100`).
- `--str-min-count <n>` String duplicate count min (default `2`).
- `--str-min-waste <bytes>` String duplicate waste min (default `0`).
- `--bfs-depth <n>` `static-refs` BFS sample depth.
- `--exact` `static-refs` full BFS.
- `-o, --output <file>` Output file.
- `-h, --help` Help.

Examples:

```bash
DumpDetective trend-analysis d1.dmp d2.dmp d3.dmp --output trends.html
DumpDetective trend-analysis C:\dumps --full --output trend.bin
DumpDetective trend-analysis --list dumps.txt --full --output report.md
DumpDetective trend-analysis d1.dmp d2.dmp d3.dmp --baseline 2 --output report.html
```

### `render`

Usage:

```text
DumpDetective render <data.json|data.bin> [options]
```

Accepted inputs:

- `report` format from any single-dump command.
- `trend-raw` format from `trend-analysis`.

Options:

- `--baseline <n>` Baseline for trend rendering (trend-raw only).
- `--ignore-event <type>` Event filter (trend-raw only, repeatable).
- `--mini` Trend summary only.
- `--from <n>` Extract dump #n sub-report from trend data.
- `--command <name>` Extract only selected command chapter(s), repeatable.
- `-o, --output <file>` Output path.
- `--format <fmt>` Format shorthand (`html|md|json|bin`).
- `-h, --help` Help.

Examples:

```bash
DumpDetective render snapshots.bin --output report.html
DumpDetective render snapshots.bin --mini --output trend-only.html
DumpDetective render snapshots.bin --from 2 --command memory-leak --output d2-memleak.html
DumpDetective render analyze-report.bin --output report.md
```

### `diff`

Usage:

```text
DumpDetective diff <before.json|before.bin> <after.json|after.bin> [options]
```

Options:

- `--key-col <n>` Table row key column index (default `0`).
- `--changed-only` Hide unchanged chapters/sections.
- `--show-same` Show unchanged rows in diff tables.
- `--command <name>` For trend-raw, diff only selected command chapter(s), repeatable.
- `--ignore-event <type>` Trend rendering event filter, repeatable.
- `-o, --output <file>` Output path (default `<before>-vs-<after>.html`).
- `-h, --help` Help.

Examples:

```bash
DumpDetective diff before.bin after.bin -o delta.html
DumpDetective diff week1.bin week2.bin --changed-only -o delta.html
DumpDetective diff week1.bin week2.bin --command memory-leak -o memleak-delta.html
```

## Memory Command Reference

### `heap-stats`

```text
Usage: DumpDetective heap-stats <dump-file> [options]
Options:
	--top <n>          Number of types to show (default 50)
	--sort <col>       Sort by: size | count | name (default size)
	--min-size <n>     Minimum total size in bytes
	--filter <str>     Type name substring filter
	--gen <gen>        Generation filter: gen0 | gen1 | gen2 | loh | poh
	-o, --output <f>   Output report file
	-h, --help         Show help
```

### `gen-summary`

```text
Usage: DumpDetective gen-summary <dump-file> [options]
Options:
	-o, --output <f>   Output report file
	-h, --help         Show help
```

### `heap-fragmentation`

```text
Usage: DumpDetective heap-fragmentation <dump-file> [options]
Options:
	-o, --output <f>   Output report file
	-h, --help         Show help
```

### `large-objects`

```text
Usage: DumpDetective large-objects <dump-file> [options]
Options:
	-n, --top <N>          Top N objects (default 50)
	-s, --min-size <bytes> Minimum object size (default 85000)
	-f, --filter <name>    Type substring filter
	-a, --addresses        Show object addresses
	--type-breakdown       Aggregate-only by type
	-o, --output <file>    Output report file
	-h, --help             Show help
```

### `pinned-objects`

```text
Usage: DumpDetective pinned-objects <dump-file> [options]
Options:
	-a, --addresses    Show object addresses (up to 100 per type)
	-o, --output <f>   Output report file
	-h, --help         Show help
```

### `memory-leak`

```text
Usage: DumpDetective memory-leak <dump-file> [options]
Options:
	-n, --top <N>         Top N types in heap-stat table (default 30)
	--min-count <N>       Min instances for suspect table (default 500)
	--no-root-trace       Skip GC root tracing
	--include-system      Include System.*/Microsoft.* suspects
	-o, --output <file>   Output report file
	-h, --help            Show help
```

### `high-refs`

```text
Usage: DumpDetective high-refs <dump-file> [options]
Options:
	--top <n>           Number of objects (default 30)
	--min-refs <n>      Minimum inbound ref count (default 10)
	--addresses         Show object addresses
	-o, --output <f>    Output report file
	-h, --help          Show help
```

### `string-duplicates`

```text
Usage: DumpDetective string-duplicates <dump-file> [options]
Options:
	--top <n>          Number of groups to show (default 50)
	--min-count <n>    Minimum duplicate count (default 2)
	--min-waste <n>    Minimum wasted bytes (default 0)
	--pattern <str>    String content substring filter
	-o, --output <f>   Output report file
	-h, --help         Show help
```

### `finalizer-queue`

```text
Usage: DumpDetective finalizer-queue <dump-file> [options]
Options:
	-n, --top <N>      Top N types (default 30)
	-a, --addresses    Show up to 20 addresses per type
	-o, --output <f>   Output report file
	-h, --help         Show help
```

### `handle-table`

```text
Usage: DumpDetective handle-table <dump-file> [options]
Options:
	-n, --top <N>       Top N types per handle kind (default 5)
	-f, --filter <k>    Filter by handle kind substring
	-o, --output <f>    Output report file
	-h, --help          Show help
```

### `static-refs`

```text
Usage: DumpDetective static-refs <dump-file> [options]
Options:
	-f, --filter <t>       Include only matching types/fields
	-e, --exclude <t>      Exclude matching types (repeatable)
	-a, --addresses        Show object addresses
	--bfs-depth <n>        Sampling depth for retained-size BFS
	-o, --output <f>       Output report file
	-h, --help             Show help
```

### `weak-refs`

```text
Usage: DumpDetective weak-refs <dump-file> [options]
Options:
	-a, --addresses    Show handle addresses
	-o, --output <f>   Output report file
	-h, --help         Show help
```

### `thread-analysis`

```text
Usage: DumpDetective thread-analysis <dump-file> [options]
Options:
	-s, --stacks          Show top stack frames per thread
	-b, --blocked-only    Show only blocked threads
	--state <s>           Filter: blocked | running | dead | all (default all)
	--name <substr>       Thread-name substring filter
	-o, --output <file>   Output report file
	-h, --help            Show help
```

### `thread-pool`

```text
Usage: DumpDetective thread-pool <dump-file> [options]
Options:
	-o, --output <file>   Output report file
	-h, --help            Show help
```

### `deadlock-detection`

```text
Usage: DumpDetective deadlock-detection <dump-file> [options]
Options:
	-o, --output <file>   Output report file
	-h, --help            Show help
```

### `async-stacks`

```text
Usage: DumpDetective async-stacks <dump-file> [options]
Options:
	-f, --filter <t>   State-machine type substring filter
	-n, --top <N>      Top N methods (default 50)
	-a, --addresses    Show state machine addresses (up to 200)
	-o, --output <f>   Output report file
	-h, --help         Show help
```

### `exception-analysis`

```text
Usage: DumpDetective exception-analysis <dump-file> [options]
Options:
	-n, --top <N>      Top N exception types (default 20)
	-f, --filter <t>   Type substring filter
	-a, --addresses    Include object addresses
	-s, --stack        Show throw stack per type
	-o, --output <f>   Output report file
	-h, --help         Show help
```

### `event-analysis`

```text
Usage: DumpDetective event-analysis <dump-file> [options]
Options:
	-n, --top <N>      Top N event fields (default 20)
	-o, --output <f>   Output report file
	-h, --help         Show help
```

### `http-requests`

```text
Usage: DumpDetective http-requests <dump-file> [options]
Options:
	-a, --addresses    Show object addresses (up to 200)
	-o, --output <f>   Output report file
	-h, --help         Show help
```

### `connection-pool`

```text
Usage: DumpDetective connection-pool <dump-file> [options]
Options:
	-a, --addresses    Show connection object addresses (up to 200)
	-o, --output <f>   Output report file
	-h, --help         Show help
```

### `wcf-channels`

```text
Usage: DumpDetective wcf-channels <dump-file> [options]
Options:
	-a, --addresses    Show object addresses
	-o, --output <f>   Output report file
	-h, --help         Show help
```

### `timer-leaks`

```text
Usage: DumpDetective timer-leaks <dump-file> [options]
Options:
	-a, --addresses    Show timer object addresses (up to 200 per type)
	-o, --output <f>   Output report file
	-h, --help         Show help
```

### `module-list`

```text
Usage: DumpDetective module-list <dump-file> [options]
Options:
	-f, --filter <t>   Module name substring filter
	--app-only         Show non-system assemblies only
	-o, --output <f>   Output report file
	-h, --help         Show help
```

### `gc-roots`

```text
Usage: DumpDetective gc-roots <dump-file> --type <typename> [options]
Options:
	-t, --type <name>       Type substring to trace
	--address <0xADDR>      Trace single object by address
	-n, --max-results <N>   Max instances for --type mode (default 10)
	--no-indirect           Skip 1-hop referrer scan
	-o, --output <f>        Output report file
	-h, --help              Show help
Note: at least one of --type or --address is required.
```

### `type-instances`

```text
Usage: DumpDetective type-instances <dump-file> --type <name> [options]
Options:
	-t, --type <name>    Type substring to search (required)
	-n, --top <N>        Max instances in detail view (default 50)
	-a, --addresses      Show individual object addresses
	--min-size <bytes>   Include only instances larger than N bytes
	--gen <0|1|2|loh>    Generation filter
	-o, --output <f>     Output report file
	-h, --help           Show help
```

### `object-inspect`

```text
Usage: DumpDetective object-inspect <dump-file> --address <hex> [options]
Options:
	--address, -x <addr>    Object address in hex (required)
	-d, --depth <N>         Recursion depth (default 5)
	--max-array <N>         Max array elements shown (default 10)
	--retained, -r          Compute retained size per reference field
	--retained-cap <N>      BFS node cap per field (default unlimited)
	--no-cache              Ignore existing BFS cache
	--no-save               Do not save BFS cache after build
	-o, --output <f>        Output report file
	-h, --help              Show help
```

### `build-bfs`

```text
Usage: DumpDetective build-bfs <dump-file-or-directory> [options]
Options:
	--force, -f    Rebuild even if cache already exists
	--recurse, -r  Recurse when input is a directory
	-h, --help     Show help
```

Examples:

```bash
DumpDetective build-bfs app.dmp
DumpDetective build-bfs app.dmp --force
DumpDetective build-bfs D:\dumps --recurse --force
DumpDetective object-inspect app.dmp -x 0x00000276DB084170 --retained
```

## Suggested Incident Triage Path

1. Run `analyze --full` and open HTML output.
2. Start with score/findings, then inspect `memory-leak`, `high-refs`, `heap-fragmentation`, `finalizer-queue`.
3. Use `gc-roots`, `type-instances`, and `object-inspect` for narrowed suspects.
4. Save `.bin` snapshots over time and compare with `diff`.
