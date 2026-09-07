using System.Text.Json;

namespace DumpDetective.Analysis.WebTrace.Parsing;

/// <summary>Which recorder produced a <c>.json</c>/<c>.json.gz</c> trace — decides which parser <see cref="WebTraceContext"/> dispatches to.</summary>
public enum WebTraceFormat
{
    /// <summary>Chrome DevTools "Enhanced Trace" (Trace Event Format) — top-level <c>traceEvents</c> array, or the array itself.</summary>
    Chrome,

    /// <summary>Firefox Profiler ("Gecko Profiler") export — top-level <c>meta</c>/<c>threads</c>/<c>shared</c> object.</summary>
    Firefox,
}

/// <summary>
/// Sniffs a trace file's top-level JSON shape to tell a Chrome DevTools trace apart
/// from a Firefox Profiler export, without buffering or fully parsing either. Both
/// formats are large top-level JSON objects (or, for legacy Chrome traces, a bare
/// array) — only their first couple of property names differ, so this reads just
/// far enough to find one of the two recognized names before giving up.
/// </summary>
public static class WebTraceFormatDetector
{
    /// <summary>Give up after this many top-level properties and assume Chrome — preserves prior behavior for anything unrecognized.</summary>
    private const int MaxPropertiesToScan = 20;

    public static WebTraceFormat Detect(string path)
    {
        using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16);
        using Stream jsonStream = WebTraceFileOpener.OpenMaybeGzip(fileStream);
        return Detect(jsonStream);
    }

    public static WebTraceFormat Detect(Stream jsonStream)
    {
        var cursor = new JsonBufferCursor(jsonStream);
        var reader = cursor.NewReader();

        if (!TryReadNext(cursor, ref reader)) return WebTraceFormat.Chrome;

        // A bare `[ {...}, {...} ]` is a legacy Chrome trace with no wrapping object at all.
        if (reader.TokenType == JsonTokenType.StartArray) return WebTraceFormat.Chrome;
        if (reader.TokenType != JsonTokenType.StartObject) return WebTraceFormat.Chrome;

        for (int i = 0; i < MaxPropertiesToScan; i++)
        {
            if (!TryReadNext(cursor, ref reader)) break;
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("traceEvents"u8)) return WebTraceFormat.Chrome;
            // "threads"/"shared" are Firefox-specific top-level keys (Chrome traces have
            // neither); "libs" alone is checked too since it appears before "threads" in
            // every Firefox export this parser has been tested against.
            if (reader.ValueTextEquals("threads"u8) || reader.ValueTextEquals("shared"u8) || reader.ValueTextEquals("libs"u8))
                return WebTraceFormat.Firefox;

            if (!TryReadNext(cursor, ref reader)) break; // advance onto the property's value
            SkipValue(cursor, ref reader);
        }

        return WebTraceFormat.Chrome;
    }

    private static bool TryReadNext(JsonBufferCursor cursor, ref Utf8JsonReader reader)
    {
        while (!reader.Read())
        {
            if (reader.IsFinalBlock) return false;
            cursor.Advance(reader);
            reader = cursor.NewReader();
        }
        return true;
    }

    private static void SkipValue(JsonBufferCursor cursor, ref Utf8JsonReader reader)
    {
        while (!reader.TrySkip())
        {
            cursor.Advance(reader);
            reader = cursor.NewReader();
        }
    }
}
