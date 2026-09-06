using DumpDetective.Analysis.WebTrace.Cache;
using DumpDetective.Analysis.WebTrace.Model;
using DumpDetective.Analysis.WebTrace.Parsing;

namespace DumpDetective.Analysis.WebTrace;

/// <summary>
/// Opens a Chrome DevTools trace once per command run. Reads the on-disk cache
/// (<see cref="WebTraceCache"/>) when it's still valid for the source file; otherwise
/// streams the raw trace through <see cref="ChromeTraceParser"/> and writes the cache
/// so every later command against the same file skips the parse entirely.
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

        var data = ChromeTraceParser.Parse(tracePath, progress: progress);
        WebTraceCache.Save(tracePath, data);
        return data;
    }
}
