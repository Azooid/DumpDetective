using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Memory.Consumers;

/// <summary>
/// Accumulates DataTable / DataRow / DataColumn / DataView / DataSet counts and
/// the addresses of the TOP-N most-populated DataTable objects during the single
/// shared heap walk, so <c>DataTableAmpAnalyzer</c> can produce results without
/// a second heap enumeration.
///
/// Row count is estimated during the walk by reading <c>nextRowID</c> — a direct
/// <c>int</c> field on DataTable that is cheap to read (one memory fetch, no object
/// dereference).  This lets us maintain a true top-N selection instead of a
/// first-N capture, which previously caused all entries to be empty schema tables.
/// </summary>
internal sealed class DataTableConsumer : IHeapObjectConsumer
{
    public long DataTableCount  { get; private set; }
    public long DataRowCount    { get; private set; }
    public long DataColumnCount { get; private set; }
    public long DataViewCount   { get; private set; }
    public long DataSetCount    { get; private set; }
    public long TotalBytes      { get; private set; }

    // Top-N DataTable addresses by nextRowID. Kept sorted: any incoming entry with a
    // higher row-id replaces the current minimum when the list is full.
    private readonly List<(ulong Addr, long Size, int NextRowId)> _top = [];
    public IReadOnlyList<(ulong Addr, long Size, int NextRowId)> TopTables => _top;

    private const int MaxTableAddrs = 500;

    public bool IsThreadSafe => false;

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        if (meta.Name is null) return;
        long size = (long)obj.Size;
        TotalBytes += size;

        // Match base class AND all TypedDataSet subclasses (e.g. FooDataSet+FooDataTable).
        if (meta.IsDataTable)
        {
            DataTableCount++;
            TrackTable(obj, meta, size);
            return;
        }

        switch (meta.Name)
        {
            case "System.Data.DataRow":    DataRowCount++;    break;
            case "System.Data.DataColumn": DataColumnCount++; break;
            case "System.Data.DataView":   DataViewCount++;   break;
            case "System.Data.DataSet":    DataSetCount++;    break;
        }
    }

    private void TrackTable(in ClrObject obj, HeapTypeMeta meta, long size)
    {
        // meta.DataTableNextRowIdField and meta.DataTableRowCollField are both pre-resolved
        // in HeapWalker.BuildMeta by walking the base-type chain — this handles both the
        // exact System.Data.DataTable type and TypedDataSet subclasses.
        int rowCount = ReadRowCountEstimate(obj, meta);

        if (_top.Count < MaxTableAddrs)
        {
            _top.Add((obj.Address, size, rowCount));
        }
        else
        {
            int minIdx = 0;
            for (int i = 1; i < _top.Count; i++)
                if (_top[i].NextRowId < _top[minIdx].NextRowId) minIdx = i;
            if (rowCount > _top[minIdx].NextRowId)
                _top[minIdx] = (obj.Address, size, rowCount);
        }
    }

    /// <summary>
    /// Estimates the number of rows in a DataTable using two independent paths.
    /// Path 1: <c>nextRowID</c> — a direct int read, no object dereferences.
    /// Path 2: <c>rowCollection → InternalDataCollectionBase.list → ArrayList._size</c>
    ///         — three reads but works when nextRowID is 0 or unresolvable.
    /// </summary>
    private static int ReadRowCountEstimate(in ClrObject obj, HeapTypeMeta meta)
    {
        // Path 1: nextRowID (cheapest — direct int read from DataTable object)
        if (meta.DataTableNextRowIdField is not null)
        {
            try
            {
                int v = meta.DataTableNextRowIdField.Read<int>(obj, interior: false);
                if (v > 0) return v;
            }
            catch { }
        }

        // Path 2: rowCollection → (inherited) list → ArrayList._size
        // DataRowCollection inherits the ArrayList 'list' field from InternalDataCollectionBase.
        // We walk the base-type chain at runtime because the field is private on the base class.
        if (meta.DataTableRowCollField is not null)
        {
            try
            {
                var rcObj = meta.DataTableRowCollField.ReadObject(obj, interior: false);
                if (rcObj.IsValid && rcObj.Type is not null)
                {
                    int count = ReadInheritedArrayListSize(rcObj);
                    if (count > 0) return count;
                }
            }
            catch { }
        }

        return 0;
    }

    /// <summary>
    /// Walks the base-type chain of <paramref name="col"/> to find a <c>list</c> or
    /// <c>_list</c> field (the ArrayList/List backing store on InternalDataCollectionBase),
    /// then reads its <c>_size</c> field.
    /// </summary>
    private static int ReadInheritedArrayListSize(ClrObject col)
    {
        for (var bt = col.Type; bt is not null; bt = bt.BaseType)
        {
            foreach (var listName in (ReadOnlySpan<string>)["list", "_list"])
            {
                var lf = bt.GetFieldByName(listName);
                if (lf is null) continue;
                try
                {
                    var listObj = lf.ReadObject(col, interior: false);
                    if (!listObj.IsValid) continue;

                    // ArrayList._size (or List<T>._size)
                    var sf = listObj.Type?.GetFieldByName("_size");
                    if (sf is not null)
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

    public void OnWalkComplete() { }

    public IHeapObjectConsumer CreateClone() => new DataTableConsumer();

    public void MergeFrom(IHeapObjectConsumer other)
    {
        var s = (DataTableConsumer)other;
        DataTableCount  += s.DataTableCount;
        DataRowCount    += s.DataRowCount;
        DataColumnCount += s.DataColumnCount;
        DataViewCount   += s.DataViewCount;
        DataSetCount    += s.DataSetCount;
        TotalBytes      += s.TotalBytes;

        // Merge the two top-N lists into a single top-N by NextRowId.
        foreach (var entry in s._top)
        {
            if (_top.Count < MaxTableAddrs)
            {
                _top.Add(entry);
            }
            else
            {
                int minIdx = 0;
                for (int i = 1; i < _top.Count; i++)
                    if (_top[i].NextRowId < _top[minIdx].NextRowId) minIdx = i;
                if (entry.NextRowId > _top[minIdx].NextRowId)
                    _top[minIdx] = entry;
            }
        }
    }
}

/// <summary>Cache key stored in ctx so <c>DataTableAmpAnalyzer</c> gets a free hit.</summary>
internal sealed class DataTableConsumerResult(
    long dataTableCount, long dataRowCount, long dataColumnCount,
    long dataViewCount,  long dataSetCount,  long totalBytes,
    IReadOnlyList<(ulong Addr, long Size, int NextRowId)> topTables)
{
    public long DataTableCount  { get; } = dataTableCount;
    public long DataRowCount    { get; } = dataRowCount;
    public long DataColumnCount { get; } = dataColumnCount;
    public long DataViewCount   { get; } = dataViewCount;
    public long DataSetCount    { get; } = dataSetCount;
    public long TotalBytes      { get; } = totalBytes;
    /// <summary>Top-N DataTable objects by estimated row count (nextRowID during walk).
    /// Field reads for column count and table name are done post-walk in the analyzer.</summary>
    public IReadOnlyList<(ulong Addr, long Size, int NextRowId)> TopTables { get; } = topTables;
}

