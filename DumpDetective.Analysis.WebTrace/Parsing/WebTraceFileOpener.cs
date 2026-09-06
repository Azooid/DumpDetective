using System.IO.Compression;

namespace DumpDetective.Analysis.WebTrace.Parsing;

/// <summary>
/// Opens a trace file's raw bytes as a (possibly gzip'd) JSON stream — shared by
/// <see cref="ChromeTraceParser"/>, <see cref="FirefoxProfileParser"/> and
/// <see cref="WebTraceFormatDetector"/> so gzip-sniffing lives in exactly one place.
/// </summary>
internal static class WebTraceFileOpener
{
    /// <summary>Wraps <paramref name="raw"/> in a <see cref="GZipStream"/> if it starts with the gzip magic bytes.</summary>
    public static Stream OpenMaybeGzip(Stream raw)
    {
        Span<byte> magic = stackalloc byte[2];
        int n = raw.Read(magic);
        raw.Position = 0;
        bool isGzip = n == 2 && magic[0] == 0x1F && magic[1] == 0x8B;
        return isGzip ? new GZipStream(raw, CompressionMode.Decompress) : raw;
    }
}
