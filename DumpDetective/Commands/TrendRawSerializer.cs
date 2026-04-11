using System.Text.Json;
using DumpDetective.Models;

namespace DumpDetective.Commands;

/// <summary>
/// Saves and loads raw <see cref="RawTrendExport"/> data (AOT-safe, no reflection).
/// </summary>
internal static class TrendRawSerializer
{
    /// <param name="subReports">
    /// Optional per-snapshot captured sub-report docs (from <c>--full</c> mode).
    /// When provided, they are embedded in each <see cref="SnapshotData.SubReport"/>.
    /// </param>
    public static void Save(IReadOnlyList<DumpDetective.Models.DumpSnapshot> snapshots,
                            string path,
                            DumpDetective.Models.ReportDoc?[]? subReports = null)
    {
        var export = new RawTrendExport
        {
            Snapshots = snapshots.Select((s, i) => SnapshotData.From(s, subReports?[i])).ToList(),
        };

        var opts = new JsonWriterOptions { Indented = true };
        using var fs     = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new Utf8JsonWriter(fs, opts);
        JsonSerializer.Serialize(writer, export, RawTrendContext.Default.RawTrendExport);
        writer.Flush();
    }

    public static RawTrendExport Load(string path)
    {
        var json   = File.ReadAllText(path, System.Text.Encoding.UTF8);
        var result = JsonSerializer.Deserialize(json, RawTrendContext.Default.RawTrendExport);
        return result ?? throw new InvalidOperationException($"File is empty or not valid raw trend data: {path}");
    }
}
