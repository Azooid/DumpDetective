using DumpDetective.Analysis.WebTrace.Cache;
using DumpDetective.Analysis.WebTrace.Model;
using DumpDetective.Analysis.WebTrace.Parsing;

namespace DumpDetective.Analysis.WebTrace;

/// <summary>
/// Opens a Chrome DevTools trace or Firefox Profiler export once per command run — the one
/// entry point every <c>web-*</c> command goes through, so which recorder produced the file
/// (<see cref="WebTraceFormatDetector"/>) is decided in exactly one place. Reads the on-disk
/// cache (<see cref="WebTraceCache"/>) when it's still valid for the source file; otherwise
/// streams the raw trace through <see cref="ChromeTraceParser"/> or <see cref="FirefoxProfileParser"/>
/// and writes the cache so every later command against the same file skips both parsing and
/// format detection entirely.
/// </summary>
public static class WebTraceContext
{
    public static WebTraceData Open(string tracePath, Action<string>? progress = null)
    {
        var cached = WebTraceCache.TryLoad(tracePath);
        if (cached is not null)
        {
            progress?.Invoke("using cached trace summary");
            return cached;
        }

        var format = WebTraceFormatDetector.Detect(tracePath);
        var data = format == WebTraceFormat.Firefox
            ? FirefoxProfileParser.Parse(tracePath, progress: progress)
            : ChromeTraceParser.Parse(tracePath, progress: progress);
        WebTraceCache.Save(tracePath, data);
        return data;
    }
}
