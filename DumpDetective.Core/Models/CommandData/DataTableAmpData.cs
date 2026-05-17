namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>DataTableAmpAnalyzer</c>.</summary>
public sealed record DataTableAmpData(
    IReadOnlyList<DataTableEntry> Tables,
    long                          TotalDataTableInstances,
    long                          TotalDataRowInstances,
    long                          TotalDataColumnInstances,
    long                          TotalDataViewInstances,
    long                          TotalDataSetInstances,
    long                          TotalManagedBytes,
    /// <summary>Estimated actual data bytes if the same data were stored in flat arrays.</summary>
    long                          EstimatedRawDataBytes,
    /// <summary>
    /// Amplification factor: TotalManagedBytes / EstimatedRawDataBytes.
    /// Values &gt; 3 indicate significant DataTable overhead.
    /// </summary>
    double                        AmplificationFactor);

/// <summary>One DataTable instance with row/column counts.</summary>
public sealed record DataTableEntry(
    ulong  Address,
    string TableName,
    int    RowCount,
    int    ColumnCount,
    long   OwnSize,
    long   EstimatedRowsSize);
