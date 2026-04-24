using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DumpDetective.Core.Models;
using DumpDetective.Core.Json;

namespace DumpDetective.Analysis;

/// <summary>
/// Saves and loads trend export files (one JSON array of <see cref="DumpSnapshot"/> records).
/// AOT-safe — uses <see cref="CoreJsonContext"/> source-gen serialization only.
/// </summary>
public static class TrendRawSerializer
{
    private sealed class Envelope
    {
        public string Format     { get; set; } = "trend-raw";
        public string ExportedAt { get; set; } = DateTime.UtcNow.ToString("o");
        public string Version    { get; set; } = "3";
        public List<DumpSnapshot> Snapshots { get; set; } = [];
    }

    public static void Save(
        IReadOnlyList<DumpSnapshot> snapshots,
        string path,
        ReportDoc?[]? subReports = null)
    {
        // Attach sub-reports before serializing, then detach to avoid mutating caller's data
        for (int i = 0; i < snapshots.Count; i++)
        {
            if (subReports is not null && i < subReports.Length && subReports[i] is not null)
                snapshots[i].SubReport = subReports[i];
        }

        var opts = new JsonWriterOptions { Indented = true };
        using var fs     = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new Utf8JsonWriter(fs, opts);

        writer.WriteStartObject();
        writer.WriteString("format",      "trend-raw");
        writer.WriteString("exportedAt", DateTime.UtcNow.ToString("o"));
        writer.WriteString("version",    "3");
        writer.WriteString("toolVersion", DumpDetective.Core.Utilities.AppInfo.Version);
        writer.WritePropertyName("snapshots");
        writer.WriteStartArray();
        foreach (var s in snapshots)
            JsonSerializer.Serialize(writer, s, CoreJsonContext.Default.DumpSnapshot);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    public static List<DumpSnapshot> Load(string path)
    {
        string json;
        if (path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            using var fs     = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var brotli = new BrotliStream(fs, CompressionMode.Decompress);
            using var reader = new StreamReader(brotli, Encoding.UTF8);
            json = reader.ReadToEnd();
        }
        else
        {
            json = File.ReadAllText(path, Encoding.UTF8);
        }

        using var doc = JsonDocument.Parse(
            json,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        if (!doc.RootElement.TryGetProperty("snapshots", out var arr))
            throw new InvalidOperationException($"File has no 'snapshots' array: {path}");

        var result = new List<DumpSnapshot>();
        foreach (var elem in arr.EnumerateArray())
        {
            var s = JsonSerializer.Deserialize(elem.GetRawText(), CoreJsonContext.Default.DumpSnapshot)
                    ?? throw new InvalidOperationException("Could not deserialize snapshot entry.");
            result.Add(s);
        }
        return result;
    }
}
