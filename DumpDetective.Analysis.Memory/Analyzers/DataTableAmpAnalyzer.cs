using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Analysis.Memory.Consumers;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Memory.Analyzers;

/// <summary>
/// Measures DataTable / DataSet memory amplification.
/// Each DataTable cell is stored as a boxed object reference in a DataRow internal array,
/// resulting in 3–8× the memory footprint of the underlying data if stored in flat arrays.
/// This analyzer counts DataTable/DataRow/DataColumn instances, reads table schema and
/// row counts via field inspection, and computes the amplification factor.
/// </summary>
public sealed class DataTableAmpAnalyzer
{
    // Column-collection field names — .NET 5+ uses the underscore-prefixed form,
    // .NET Framework uses the bare form.
    private static readonly string[] ColCollectionFields =
        ["_columnCollection", "columnCollection", "_columns", "columns"];

    // Fallback: direct integer count fields on a collection object
    private static readonly string[] DirectCountFields   = ["_count", "_size", "count", "m_count"];

    public DataTableAmpData Analyze(DumpContext ctx, int top = 100)
    {
        if (!ctx.Heap.CanWalkHeap)
            return new DataTableAmpData([], 0, 0, 0, 0, 0, 0, 0, 0);
        // Fast path: data was already collected during the main heap walk.
        if (ctx.GetAnalysis<DataTableConsumerResult>() is { } cached)
            return BuildFromCache(cached, ctx, top);

        // Slow path: standalone invocation without a prior full collection.
        // Collect addresses first, then do field reads and sort by row count.
        long dataTableCount = 0, dataRowCount = 0, dataColumnCount = 0;
        long dataViewCount = 0, dataSetCount = 0;
        long totalBytes = 0;
        // Reuse the same top-N logic as the consumer: keep the 200 most-populated
        // tables (by nextRowID) rather than the first 200 by address order.
        var topTables = new List<(ulong Addr, long Size, int NextRowId)>(501);
        ClrInstanceField? nextRowIdField = null;
        bool nextRowIdLookedUp = false;

        CommandBase.RunStatus("Scanning DataTable objects...", update =>
        {
            long scanned = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null) continue;
                string name = obj.Type.Name ?? string.Empty;
                scanned++;
                if ((scanned & 0x3FFF) == 0 && sw.ElapsedMilliseconds >= 200)
                {
                    update($"Scanning \u2014 {scanned:N0} objects  \u2022  {dataTableCount:N0} DataTables...");
                    sw.Restart();
                }

                long size = (long)obj.Size;
                totalBytes += size;

                if (name == "System.Data.DataTable")
                {
                    dataTableCount++;
                    if (!nextRowIdLookedUp)
                    {
                        nextRowIdField = obj.Type.GetFieldByName("nextRowID")
                                      ?? obj.Type.GetFieldByName("_nextRowID");
                        nextRowIdLookedUp = true;
                    }
                    int nextRowId = 0;
                    if (nextRowIdField?.ElementType is ClrElementType.Int32)
                    {
                        try { nextRowId = nextRowIdField.Read<int>(obj, interior: false); }
                        catch { }
                    }
                    if (topTables.Count < 500)
                    {
                        topTables.Add((obj.Address, size, nextRowId));
                    }
                    else
                    {
                        int minIdx = 0;
                        for (int i = 1; i < topTables.Count; i++)
                            if (topTables[i].NextRowId < topTables[minIdx].NextRowId) minIdx = i;
                        if (nextRowId > topTables[minIdx].NextRowId)
                            topTables[minIdx] = (obj.Address, size, nextRowId);
                    }
                }
                else if (name == "System.Data.DataRow")    dataRowCount++;
                else if (name == "System.Data.DataColumn") dataColumnCount++;
                else if (name == "System.Data.DataView")   dataViewCount++;
                else if (name == "System.Data.DataSet")    dataSetCount++;
            }
        });

        if (dataTableCount == 0)
            return new DataTableAmpData([], 0, 0, 0, 0, 0, 0, 0, 1.0);

        // Read fields for top-N captured tables, sort by actual row count, keep top N.
        var tables = BuildTableEntries(ctx.Heap, topTables, top);

        long totalRows = tables.Sum(t => (long)t.RowCount);
        if (totalRows == 0) totalRows = dataRowCount;
        long avgCols = tables.Count > 0 && tables.Sum(t => (long)t.ColumnCount) > 0
            ? Math.Max(1, tables.Sum(t => (long)t.ColumnCount) / tables.Count)
            : (dataTableCount > 0 && dataColumnCount > 0 ? Math.Max(1, dataColumnCount / dataTableCount) : 1);

        long estimatedRaw = totalRows * avgCols * 8;
        long managedTotal = dataTableCount * 200 + dataRowCount * 120 +
                            dataColumnCount * 250 + totalRows * avgCols * 8;
        double amp = managedTotal > 0 && estimatedRaw > 0
            ? (double)managedTotal / estimatedRaw : 1.0;

        return new DataTableAmpData(tables, dataTableCount, dataRowCount, dataColumnCount,
            dataViewCount, dataSetCount, managedTotal, estimatedRaw, Math.Round(amp, 1));
    }

    /// <summary>
    /// Reads row/col/name fields for each captured address, sorts by row count descending,
    /// and returns the top-N entries.
    /// </summary>
    private static List<DataTableEntry> BuildTableEntries(
        ClrHeap heap,
        IReadOnlyList<(ulong Addr, long Size, int NextRowId)> addrs,
        int top)
    {
        var raw = new List<DataTableEntry>(addrs.Count);
        foreach (var (addr, size, hintRows) in addrs)
        {
            var obj = heap.GetObject(addr);
            if (!obj.IsValid) continue;
            var entry = BuildTableEntry(heap, obj, size, hintRows);
            if (entry is not null) raw.Add(entry);
        }
        raw.Sort((a, b) => b.RowCount.CompareTo(a.RowCount));
        return raw.Count <= top ? raw : raw.GetRange(0, top);
    }

    /// <summary>Overload used by the slow path which only has (Addr, Size).</summary>
    private static List<DataTableEntry> BuildTableEntries(
        ClrHeap heap,
        IReadOnlyList<(ulong Addr, long Size)> addrs,
        int top)
    {
        var raw = new List<DataTableEntry>(addrs.Count);
        foreach (var (addr, size) in addrs)
        {
            var obj = heap.GetObject(addr);
            if (!obj.IsValid) continue;
            var entry = BuildTableEntry(heap, obj, size, 0);
            if (entry is not null) raw.Add(entry);
        }
        raw.Sort((a, b) => b.RowCount.CompareTo(a.RowCount));
        return raw.Count <= top ? raw : raw.GetRange(0, top);
    }

    /// <summary>
    /// Searches for a field by name in the type's OWN fields first, then walks the
    /// BaseType chain.  Required for TypedDataSet subclasses where fields like
    /// <c>nextRowID</c>, <c>rowCollection</c>, and <c>_columnCollection</c> are
    /// declared on <c>System.Data.DataTable</c>, not on the concrete subtype.
    /// </summary>
    private static ClrInstanceField? FindField(ClrType? type, string name)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            var f = t.GetFieldByName(name);
            if (f is not null) return f;
        }
        return null;
    }

    private static DataTableEntry? BuildTableEntry(ClrHeap heap, ClrObject obj, long size, int hintRows)
    {
        try
        {
            string tableName = "<unknown>";
            int rowCount = 0, colCount = 0;

            // ── Table name ────────────────────────────────────────────────────
            // tableName / _tableName is declared on System.Data.DataTable; for
            // TypedDataSet subclasses we must walk the base-type chain.
            var nameField = FindField(obj.Type, "tableName")
                         ?? FindField(obj.Type, "_tableName");
            if (nameField is not null)
                tableName = obj.ReadStringField(nameField.Name ?? "tableName") ?? "<unknown>";

            // ── Row count ─────────────────────────────────────────────────────
            // Path 1: nextRowID / _nextRowID via base-type chain walk
            var nextRowField = FindField(obj.Type, "nextRowID")
                            ?? FindField(obj.Type, "_nextRowID");
            if (nextRowField is not null)
            {
                try { rowCount = nextRowField.Read<int>(obj, interior: false); }
                catch { }
            }

            // Path 2: rowCollection → InternalDataCollectionBase.list → ArrayList._size
            // This is independent of nextRowID and works even if the table was Clear()-ed
            // and refilled (where nextRowID keeps going up but actual row count is visible here).
            if (rowCount == 0)
            {
                var rcField = FindField(obj.Type, "rowCollection")
                           ?? FindField(obj.Type, "_rowCollection");
                if (rcField is not null)
                {
                    try
                    {
                        var rcObj = rcField.ReadObject(obj, interior: false);
                        if (rcObj.IsValid)
                            rowCount = ReadCollectionCountInherited(rcObj);
                    }
                    catch { }
                }
            }

            // Path 3: hint from the heap-walk consumer (may also be 0 if both paths above failed)
            if (rowCount == 0 && hintRows > 0)
                rowCount = hintRows;

            // ── Column count ──────────────────────────────────────────────────
            // _columnCollection / columnCollection are also on System.Data.DataTable.
            foreach (var ccfn in ColCollectionFields)
            {
                var ccField = FindField(obj.Type, ccfn);
                if (ccField is null) continue;
                var ccObj = ccField.ReadObject(obj, interior: false);
                if (!ccObj.IsValid) continue;

                // Modern .NET: ._list (List<DataColumn>) → ._size
                var listField = ccObj.Type?.GetFieldByName("_list");
                if (listField is not null)
                {
                    try
                    {
                        var listObj = listField.ReadObject(ccObj, interior: false);
                        if (listObj.IsValid)
                        {
                            var sf = listObj.Type?.GetFieldByName("_size");
                            if (sf?.ElementType is ClrElementType.Int32)
                            { colCount = sf.Read<int>(listObj, interior: false); break; }
                        }
                    }
                    catch { }
                }

                // Fallback: count fields on the collection itself or via inheritance
                colCount = ReadCollectionCountInherited(ccObj);
                if (colCount > 0) break;
            }

            long rowsSize = (long)rowCount * Math.Max(1, colCount) * 40;
            return new DataTableEntry(obj.Address, tableName, rowCount, colCount, size, rowsSize);
        }
        catch { return null; }
    }

    /// <summary>
    /// Reads the element count from a collection object, searching both the object's
    /// own fields AND inherited fields up the type hierarchy.
    /// Covers: ArrayList (_size on own type), InternalDataCollectionBase (list → ArrayList._size),
    /// List&lt;T&gt; (_size), and similar patterns.
    /// </summary>
    private static int ReadCollectionCountInherited(ClrObject col)
    {
        // Direct count fields on the object itself first
        foreach (var name in DirectCountFields)
        {
            var f = col.Type?.GetFieldByName(name);
            if (f?.ElementType is ClrElementType.Int32)
            {
                try { int v = f.Read<int>(col, interior: false); if (v >= 0) return v; }
                catch { }
            }
        }

        // Walk base types — the count field may be declared on InternalDataCollectionBase
        // or another ancestor, and GetFieldByName only searches the current type.
        for (var baseType = col.Type?.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            // Try direct count field on the base type
            foreach (var name in DirectCountFields)
            {
                var f = baseType.GetFieldByName(name);
                if (f?.ElementType is ClrElementType.Int32)
                {
                    try { int v = f.Read<int>(col, interior: false); if (v >= 0) return v; }
                    catch { }
                }
            }

            // Try _list / list field → ArrayList / List<T> → ._size
            foreach (var listName in new[] { "_list", "list" })
            {
                var listField = baseType.GetFieldByName(listName);
                if (listField is null) continue;
                try
                {
                    var listObj = listField.ReadObject(col, interior: false);
                    if (!listObj.IsValid) continue;
                    var sf = listObj.Type?.GetFieldByName("_size");
                    if (sf?.ElementType is ClrElementType.Int32)
                    {
                        int v = sf.Read<int>(listObj, interior: false);
                        if (v >= 0) return v;
                    }
                }
                catch { }
            }
        }

        return 0;
    }

    private static DataTableAmpData BuildFromCache(DataTableConsumerResult r, DumpContext ctx, int top)
    {
        if (r.DataTableCount == 0)
            return new DataTableAmpData([], 0, 0, 0, 0, 0, 0, 0, 1.0);

        // Read fields for all captured tables (already top-N by nextRowId from consumer),
        // sort by actual row count (which may refine the estimate via rowCollection traversal),
        // and keep top N.
        var tables = BuildTableEntries(ctx.Heap, r.TopTables, top);

        // Per-table row/col counts may be 0 if field reads fail on the target runtime.
        // Fall back to globally-counted DataRow / DataColumn object totals.
        long totalRows = tables.Sum(t => (long)t.RowCount);
        if (totalRows == 0) totalRows = r.DataRowCount;

        long perTableColSum = tables.Sum(t => (long)t.ColumnCount);
        long avgCols = (tables.Count > 0 && perTableColSum > 0)
            ? Math.Max(1, perTableColSum / tables.Count)
            : (r.DataTableCount > 0 && r.DataColumnCount > 0
                ? Math.Max(1, r.DataColumnCount / r.DataTableCount)
                : 1);

        long estimatedRaw  = totalRows * avgCols * 8;
        long managedTotal  = r.DataTableCount * 200 + r.DataRowCount * 120 +
                             r.DataColumnCount * 250 + totalRows * avgCols * 8;
        double amp = managedTotal > 0 && estimatedRaw > 0
            ? (double)managedTotal / estimatedRaw : 1.0;

        return new DataTableAmpData(tables, r.DataTableCount, r.DataRowCount, r.DataColumnCount,
            r.DataViewCount, r.DataSetCount, managedTotal, estimatedRaw, Math.Round(amp, 1));
    }
}
