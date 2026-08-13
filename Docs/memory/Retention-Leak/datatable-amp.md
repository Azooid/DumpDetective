# datatable-amp

**Category:** Retention / Leak Signals  
**Included in `analyze --full`:** Yes

## What it does

Counts `DataTable`, `DataRow`, `DataColumn`, `DataView`, and `DataSet` instances on the managed heap. Reads table name, row count, and column count via field inspection. Computes a memory amplification factor: managed overhead vs. estimated raw data size. A large amplification factor indicates that DataTable's per-row object model is consuming significantly more memory than the actual data warrants.

DataTable is notorious for allocating one object per cell (via boxing in the `object[]` backing store), making it 10–30× more memory-intensive than a typed list or array for the same data.

---

## Analyzer: `DataTableAmpAnalyzer`

**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

Reads `DataTable._rows` to count `DataRow` objects, then reads `DataColumn._columnName` and `DataColumn.DataType` to characterize schema width and type mix. Amplification is calculated as: `total DataTable heap bytes / (rowCount × columnCount × estimatedBytesPerValue)`, where `estimatedBytesPerValue` assumes 8 bytes for value types and 16 bytes for strings. Tables are sorted by managed size descending.

### Consumer: `DataTableConsumer`

Accumulates `DataTable` and `DataSet` instance counts during the shared heap walk.

---

## Options

| Option | Description |
|---|---|
| `<dump>` | Path to `.dmp` or `.mdmp` file |
| `--top <n>` | Max `DataTable` instances to show in the report (default: `200`) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |
| `-h, --help` | Show this help |

---

## What to look for

| Signal | What it means |
|---|---|
| Amplification factor > 15× | Strong case to replace `DataTable` with a typed model |
| Many `DataSet` objects still in memory | Result sets not being disposed or short-lived enough |
| `DataTable` with millions of rows | Consider paging or streaming instead of loading into memory |
| High `DataView` count relative to `DataTable` count | Excessive filter/sort views created and not disposed |
