using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class DataTableAmpReport
{
    public void Render(DataTableAmpData data, IRenderSink sink)
    {
        sink.Section("DataTable Memory Amplification");
        sink.Explain(
            what: "DataTable / DataSet store each cell value as a boxed object reference in an internal row array. " +
                  "This boxing overhead means DataTable can consume 3–8× more memory than the same data stored in " +
                  "typed List<T> or arrays.",
            why:  "Each DataRow holds a reference array (one slot per column) plus separate bookkeeping for " +
                  "original values, row state, and versioning. A table with 100,000 rows and 10 columns may " +
                  "consume 200–400 MB for what could be stored in 8 MB of typed arrays.",
            bullets:
            [
                "High row × column count → exponential memory growth vs. typed collections",
                "DataView instances → each DataView holds its own indexed copy of the row references",
                "DataSet containing many tables → aggregate overhead multiplies",
                "DataTable in Gen2 / LOH → table is long-lived; not being released after use",
            ],
            impact: "High DataTable memory pressure causes frequent Gen2 GC, long GC pause times, " +
                    "and eventual OutOfMemoryException in data-heavy workloads.",
            action: "Replace DataTable with typed List<T>, record arrays, or IAsyncEnumerable<T> for streaming. " +
                    "If DataTable is required for DataBind compatibility, dispose it explicitly after use " +
                    "or avoid caching it in long-lived fields.");

        if (data.TotalDataTableInstances == 0)
        {
            sink.Alert(AlertLevel.Info, "No DataTable/DataSet objects found on the managed heap.");
            return;
        }

        sink.KeyValues([
            ("DataTable instances",  data.TotalDataTableInstances.ToString("N0")),
            ("DataRow instances",    data.TotalDataRowInstances.ToString("N0")),
            ("DataColumn instances", data.TotalDataColumnInstances.ToString("N0")),
            ("DataView instances",   data.TotalDataViewInstances.ToString("N0")),
            ("DataSet instances",    data.TotalDataSetInstances.ToString("N0")),
            ("Est. managed overhead", DumpHelpers.FormatSize(data.TotalManagedBytes)),
            ("Est. raw data size",   DumpHelpers.FormatSize(data.EstimatedRawDataBytes)),
            ("Amplification factor", $"{data.AmplificationFactor:F1}×"),
        ]);

        if (data.AmplificationFactor >= 4)
            sink.Alert(AlertLevel.Critical,
                $"DataTable amplification factor: {data.AmplificationFactor:F1}× — " +
                "DataTable overhead is consuming significantly more memory than the underlying data.",
                advice: "Replace with typed List<T> or record arrays. " +
                        "For read-only scenarios, use IAsyncEnumerable<T> to avoid materializing all rows at once.");
        else if (data.AmplificationFactor >= 2)
            sink.Alert(AlertLevel.Warning,
                $"DataTable amplification factor: {data.AmplificationFactor:F1}×. " +
                "Consider typed collections for memory-sensitive paths.");

        if (data.Tables.Count == 0) return;

        sink.Section("Largest DataTable Instances");
        var rows = data.Tables
            .OrderByDescending(t => t.RowCount)
            .Select(t => new[]
            {
                $"0x{t.Address:X}",
                t.TableName.Length > 40 ? t.TableName[..40] + "\u2026" : t.TableName,
                t.RowCount.ToString("N0"),
                t.ColumnCount.ToString("N0"),
                DumpHelpers.FormatSize(t.OwnSize),
                DumpHelpers.FormatSize(t.EstimatedRowsSize),
            })
            .ToList();

        sink.Table(
            ["Address", "Table Name", "Rows", "Columns", "Object Size", "Est. Row Data"],
            rows,
            "Est. Row Data = rows × columns × 40 bytes avg per cell. Actual may vary by column type.");
    }
}
