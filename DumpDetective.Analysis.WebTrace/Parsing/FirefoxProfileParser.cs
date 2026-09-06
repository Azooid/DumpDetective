using System.Text.Json;
using DumpDetective.Analysis.WebTrace.Model;

namespace DumpDetective.Analysis.WebTrace.Parsing;

/// <summary>
/// Reduces a Firefox Profiler ("Gecko Profiler") export into the same <see cref="WebTraceData"/>
/// shape <see cref="ChromeTraceParser"/> produces from a Chrome DevTools trace — every command
/// under <c>DumpDetective.Commands.Web</c> works against either recorder without knowing which
/// one it got.
///
/// Firefox's format is columnar (parallel arrays sharing an index, e.g. <c>frameTable.func[i]</c>
/// names the row of <c>funcTable</c> frame <c>i</c> belongs to), not a flat event stream, so it
/// can't reuse <see cref="ChromeTraceParser"/>'s single-pass token-by-token approach — that
/// approach exists specifically to avoid buffering a Chrome trace's flat <c>traceEvents</c>
/// array, which has no such structure to exploit. Here the whole document is parsed into a
/// <see cref="JsonDocument"/> instead; the shared lookup tables (funcTable/frameTable/stackTable,
/// a few hundred KB even for a large recording) and each thread's samples/markers are all small
/// relative to a modern machine's memory, so the "one pass over the raw stream" rule that matters
/// for Chrome's 200MB+ traces doesn't buy anything proportionally similar here. Revisit only if a
/// captured profile is ever too large for this to hold in memory.
///
/// What this parser maps, and what it deliberately leaves empty (documented, not silently
/// dropped, so a command doesn't imply data was captured and found empty when the format simply
/// doesn't carry it):
///   - <see cref="WebTraceData.CpuHotspots"/> — every thread's stack samples, keyed the same way
///     Chrome's V8 profiler samples are (url|function|line), so both recorders feed the exact
///     same hotspot report. There is no reliable "this sample was idle" signal to exclude here —
///     see the note on <see cref="ProcessSamples"/> — so, exactly like Chrome's profiler samples,
///     every leaf frame is counted; a thread genuinely idle in an OS wait syscall just shows up
///     as self-time on that syscall's frame, the same way it would in any native profiler.
///   - <see cref="WebTraceData.LongTasks"/> — Firefox has no direct equivalent of Chrome's
///     <c>RunTask</c> span. Instead this uses the signal Firefox's own sampler computes for
///     exactly this purpose: <c>samples.eventDelay</c>, "how long the oldest queued input event
///     has been waiting as of this sample" (see <see cref="ProcessSamples"/>).
///   - <see cref="WebTraceData.GcEvents"/> — from <c>GCMajor</c>/<c>GCMinor</c>/<c>GCSlice</c>
///     markers, which (unlike most Firefox markers) really do carry a real pause duration.
///   - <see cref="WebTraceData.InputLatencyEvents"/> — from <c>DOMEvent</c> markers' own
///     <c>latency</c> field, the same "dispatch to processed" gap Chrome's <c>InputLatency::*</c>
///     events measure.
///   - <see cref="WebTraceData.Counters"/>, <see cref="WebTraceData.NetworkRequests"/>,
///     <see cref="WebTraceData.Screenshots"/>, begin/dropped frame counts — left empty. Firefox's
///     <c>malloc</c> counter is native allocator bytes, not the DOM node/listener/JS-heap counts
///     Chrome's <c>UpdateCounters</c> carries, so folding it in would silently mislabel a
///     different measurement as the same one; network/screenshot markers weren't present in any
///     profile this parser was built against, so mapping them without a confirmed shape risked
///     fabricating data rather than reporting it. web-memory-leak, web-network and web-jank still
///     run against a Firefox trace — they just report "no data captured" instead of asserting a
///     count that would be wrong.
/// </summary>
public static class FirefoxProfileParser
{
    public const long DefaultLongTaskFloorUs = ChromeTraceParser.DefaultLongTaskFloorUs;

    public static WebTraceData Parse(string path, long longTaskFloorUs = DefaultLongTaskFloorUs, Action<string>? progress = null)
    {
        using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16);
        using Stream jsonStream = WebTraceFileOpener.OpenMaybeGzip(fileStream);
        return Parse(jsonStream, path, longTaskFloorUs, progress);
    }

    public static WebTraceData Parse(Stream jsonStream, string sourcePath, long longTaskFloorUs, Action<string>? progress)
    {
        progress?.Invoke("parsing profile...");
        using var doc = JsonDocument.Parse(jsonStream);
        var root = doc.RootElement;

        var strings = ReadStrings(root.GetProperty("shared").GetProperty("stringArray"));

        progress?.Invoke("indexing shared tables...");
        var funcTables = BuildFuncTables(root.GetProperty("shared"), strings);
        var frameTables = BuildFrameTables(root.GetProperty("shared"));

        var hotspots = new Dictionary<string, WebCpuHotspot>(StringComparer.Ordinal);
        var longTasks = new List<WebLongTask>();
        var gcEvents = new List<WebGcEvent>();
        var inputLatencyEvents = new List<WebInputLatencyEvent>();
        long minTsUs = long.MaxValue, maxTsUs = long.MinValue;
        long totalSamplesScanned = 0;

        int threadIndex = 0;
        foreach (var thread in root.GetProperty("threads").EnumerateArray())
        {
            threadIndex++;
            if (progress is not null && threadIndex % 5 == 0)
                progress($"processed {threadIndex:N0} threads...");

            bool isMainThread = thread.TryGetProperty("isMainThread", out var mtEl) && mtEl.ValueKind == JsonValueKind.True;
            int pid = ParseProcessOrThreadId(thread, "pid");
            int tid = ParseProcessOrThreadId(thread, "tid");

            if (thread.TryGetProperty("samples", out var samples))
                totalSamplesScanned += ProcessSamples(
                    samples, isMainThread, pid, tid, longTaskFloorUs,
                    frameTables, funcTables, hotspots, longTasks, ref minTsUs, ref maxTsUs);

            if (thread.TryGetProperty("markers", out var markers))
                ProcessMarkers(markers, strings, gcEvents, inputLatencyEvents, ref minTsUs, ref maxTsUs);
        }

        return new WebTraceData
        {
            SourcePath         = sourcePath,
            Counters           = [],
            CpuHotspots        = hotspots.Values.OrderByDescending(h => h.SelfTimeUs).ToList(),
            LongTasks          = longTasks,
            GcEvents           = gcEvents,
            Screenshots        = [],
            NetworkRequests    = [],
            InputLatencyEvents = inputLatencyEvents,
            TotalEventsScanned = totalSamplesScanned,
            DurationUs         = maxTsUs > minTsUs ? maxTsUs - minTsUs : 0,
        };
    }

    // ── per-thread sample walk ──────────────────────────────────────────────

    /// <summary>
    /// Tracks the current run of samples with a positive <c>eventDelay</c> — Firefox's own
    /// "how long has the oldest pending input event been waiting" metric, recomputed every
    /// sample. A run's peak delay <em>is</em> approximately how long the main thread was
    /// unresponsive for: if delay was 0 at sample i, climbed to a peak P by sample j, then
    /// dropped back to ~0 at sample j+1, the main thread was blocked from roughly
    /// <c>timestamp(j) - P</c> to <c>timestamp(j)</c> — the queued event couldn't be processed
    /// until whatever was running finished, at which point the backlog drains and delay resets.
    /// This is Firefox's closest equivalent to Chrome's explicit <c>RunTask</c> span duration.
    /// </summary>
    private sealed class DelayRun
    {
        public double PeakDelayMs;
        public long   PeakTsUs;
        public readonly Dictionary<string, long> SelfTimeUs = new(StringComparer.Ordinal);

        public void Reset()
        {
            PeakDelayMs = 0;
            SelfTimeUs.Clear();
        }
    }

    private static long ProcessSamples(
        JsonElement samples, bool isMainThread, int pid, int tid, long longTaskFloorUs,
        FrameTables frames, FuncTables funcs,
        Dictionary<string, WebCpuHotspot> hotspots, List<WebLongTask> longTasks,
        ref long minTsUs, ref long maxTsUs)
    {
        if (!samples.TryGetProperty("stack", out var stackArr) || !samples.TryGetProperty("timeDeltas", out var deltaArr))
            return 0;

        // eventDelay is optional (older exports, or threads Firefox never tracks responsiveness
        // for) — long-task derivation is simply skipped for this thread when absent, rather than
        // guessing at busy/idle from anything else. See the type-level doc comment for why frame
        // category can't be used for that: it defaults to 0 ("Idle" in the category list, purely
        // by coincidence of ordering) for the vast majority of arbitrary native leaf frames, not
        // just genuinely idle ones.
        bool hasEventDelay = samples.TryGetProperty("eventDelay", out var delayArr) && isMainThread;

        var run = new DelayRun();
        double clockMs = 0;
        long scanned = 0;

        using var stackIter = stackArr.EnumerateArray();
        using var deltaIter = deltaArr.EnumerateArray();
        using var delayIter = hasEventDelay ? delayArr.EnumerateArray() : default;
        bool haveDelayIter = hasEventDelay;

        while (stackIter.MoveNext() & deltaIter.MoveNext() & (!haveDelayIter || delayIter.MoveNext()))
        {
            scanned++;
            double deltaMs = deltaIter.Current.ValueKind == JsonValueKind.Number ? deltaIter.Current.GetDouble() : 0;
            clockMs += deltaMs;
            if (deltaMs <= 0) continue;

            long deltaUs     = (long)(deltaMs * 1000);
            long sampleEndUs = (long)(clockMs * 1000);
            if (sampleEndUs < minTsUs) minTsUs = sampleEndUs;
            if (sampleEndUs > maxTsUs) maxTsUs = sampleEndUs;

            string? key = null;
            if (stackIter.Current.ValueKind == JsonValueKind.Number)
            {
                int stackIdx = stackIter.Current.GetInt32();
                if (stackIdx >= 0 && stackIdx < frames.StackFrame.Length)
                {
                    int frameIdx = frames.StackFrame[stackIdx];
                    if (frameIdx >= 0 && frameIdx < frames.FrameFunc.Length)
                    {
                        int funcIdx = frames.FrameFunc[frameIdx];
                        if (funcIdx >= 0 && funcIdx < funcs.Name.Length)
                        {
                            int line = frames.FrameLine[frameIdx] ?? funcs.LineNumber[funcIdx] ?? -1;
                            key = $"{funcs.Url[funcIdx]}|{funcs.Name[funcIdx]}|{line}";
                            if (!hotspots.TryGetValue(key, out var h))
                                hotspots[key] = h = new WebCpuHotspot { Url = funcs.Url[funcIdx], FunctionName = funcs.Name[funcIdx], Line = line };
                            h.SelfTimeUs  += deltaUs;
                            h.SampleCount += 1;
                        }
                    }
                }
            }

            if (!haveDelayIter) continue;

            double delayMs = delayIter.Current.ValueKind == JsonValueKind.Number ? delayIter.Current.GetDouble() : 0;
            if (delayMs > 0)
            {
                if (delayMs > run.PeakDelayMs) { run.PeakDelayMs = delayMs; run.PeakTsUs = sampleEndUs; }
                if (key is not null)
                {
                    run.SelfTimeUs.TryGetValue(key, out long existing);
                    run.SelfTimeUs[key] = existing + deltaUs;
                }
            }
            else
            {
                FlushDelayRun(run, longTaskFloorUs, pid, tid, hotspots, longTasks);
            }
        }

        FlushDelayRun(run, longTaskFloorUs, pid, tid, hotspots, longTasks);
        return scanned;
    }

    /// <summary>Emits the in-progress delay run as a <see cref="WebLongTask"/> if its peak clears the floor, attributing the run's single largest self-time contributor.</summary>
    private static void FlushDelayRun(
        DelayRun run, long longTaskFloorUs, int pid, int tid,
        Dictionary<string, WebCpuHotspot> hotspots, List<WebLongTask> longTasks)
    {
        long peakDelayUs = (long)(run.PeakDelayMs * 1000);
        if (peakDelayUs >= longTaskFloorUs)
        {
            var task = new WebLongTask
            {
                TimestampUs = Math.Max(0, run.PeakTsUs - peakDelayUs),
                DurationUs  = peakDelayUs,
                Pid         = pid,
                Tid         = tid,
            };
            if (run.SelfTimeUs.Count > 0)
            {
                var top = run.SelfTimeUs.OrderByDescending(kv => kv.Value).First();
                if (hotspots.TryGetValue(top.Key, out var h))
                {
                    task.AttributedFunction   = h.FunctionName;
                    task.AttributedUrl        = h.Url;
                    task.AttributedLine       = h.Line;
                    task.AttributedSelfTimeUs = top.Value;
                }
            }
            longTasks.Add(task);
        }
        run.Reset();
    }

    // ── markers (GC pauses, input latency) ──────────────────────────────────

    private static void ProcessMarkers(
        JsonElement markers, string[] strings,
        List<WebGcEvent> gcEvents, List<WebInputLatencyEvent> inputLatencyEvents,
        ref long minTsUs, ref long maxTsUs)
    {
        if (!markers.TryGetProperty("name", out var nameArr) ||
            !markers.TryGetProperty("startTime", out var startArr) ||
            !markers.TryGetProperty("endTime", out var endArr) ||
            !markers.TryGetProperty("phase", out var phaseArr) ||
            !markers.TryGetProperty("data", out var dataArr))
            return;

        using var nameIter  = nameArr.EnumerateArray();
        using var startIter = startArr.EnumerateArray();
        using var endIter   = endArr.EnumerateArray();
        using var phaseIter = phaseArr.EnumerateArray();
        using var dataIter  = dataArr.EnumerateArray();

        while (nameIter.MoveNext() & startIter.MoveNext() & endIter.MoveNext() & phaseIter.MoveNext() & dataIter.MoveNext())
        {
            string name = ResolveMarkerName(nameIter.Current, strings);
            double startMs = startIter.Current.ValueKind == JsonValueKind.Number ? startIter.Current.GetDouble() : 0;
            double endMs   = endIter.Current.ValueKind   == JsonValueKind.Number ? endIter.Current.GetDouble()   : 0;
            int phase = phaseIter.Current.ValueKind == JsonValueKind.Number ? phaseIter.Current.GetInt32() : 0;

            long startUs = (long)(startMs * 1000);
            if (startUs > 0)
            {
                if (startUs < minTsUs) minTsUs = startUs;
                if (startUs > maxTsUs) maxTsUs = startUs;
            }

            // phase 1 ("Interval") is the only shape where startTime/endTime are both a real,
            // already-closed pair on this one row — phase 2/3 (open start/end halves that
            // never got collapsed) don't carry a trustworthy duration and are skipped.
            if (phase != 1 || endMs <= startMs) continue;
            long durationUs = (long)((endMs - startMs) * 1000);

            switch (name)
            {
                case "GCMajor": gcEvents.Add(new WebGcEvent(startUs, durationUs, "Major")); break;
                case "GCMinor": gcEvents.Add(new WebGcEvent(startUs, durationUs, "Minor")); break;
                case "GCSlice": gcEvents.Add(new WebGcEvent(startUs, durationUs, "Slice")); break;

                case "DOMEvent" when dataIter.Current.ValueKind == JsonValueKind.Object:
                    string kind = dataIter.Current.TryGetProperty("eventType", out var etEl) && etEl.ValueKind == JsonValueKind.String
                        ? etEl.GetString() ?? "Unknown"
                        : "Unknown";
                    double latencyMs = dataIter.Current.TryGetProperty("latency", out var latEl) && latEl.ValueKind == JsonValueKind.Number
                        ? latEl.GetDouble()
                        : endMs - startMs;
                    if (latencyMs > 0)
                        inputLatencyEvents.Add(new WebInputLatencyEvent { TimestampUs = startUs, DurationUs = (long)(latencyMs * 1000), Kind = kind });
                    break;
            }
        }
    }

    private static string ResolveMarkerName(JsonElement nameEl, string[] strings) => nameEl.ValueKind switch
    {
        JsonValueKind.Number when nameEl.TryGetInt32(out int idx) && idx >= 0 && idx < strings.Length => strings[idx],
        JsonValueKind.String => nameEl.GetString() ?? "",
        _ => "",
    };

    // ── shared table indexing ───────────────────────────────────────────────

    private readonly record struct FuncTables(string[] Name, string[] Url, int?[] LineNumber);
    private readonly record struct FrameTables(int[] StackFrame, int[] FrameFunc, int?[] FrameLine);

    private static FuncTables BuildFuncTables(JsonElement shared, string[] strings)
    {
        var funcTable = shared.GetProperty("funcTable");
        var name       = ReadIndirectStrings(funcTable.GetProperty("name"), strings).Select(s => s ?? "(anonymous)").ToArray();
        var resource   = ReadNullableInts(funcTable.GetProperty("resource"));
        var lineNumber = ReadNullableInts(funcTable.GetProperty("lineNumber"));

        // "source" (index into shared.sources, giving the original file path) is the current
        // schema; a profile recorded by an older Firefox build may only have "fileName" directly
        // on funcTable instead — both are read so this doesn't silently lose every URL against
        // an older export.
        string?[]? sourceFilenames = null;
        int?[]? sourceIndex = null;
        if (funcTable.TryGetProperty("source", out var sourceEl) && shared.TryGetProperty("sources", out var sources))
        {
            sourceIndex     = ReadNullableInts(sourceEl);
            sourceFilenames = ReadIndirectStrings(sources.GetProperty("filename"), strings);
        }
        string?[]? legacyFileName = funcTable.TryGetProperty("fileName", out var fileNameEl)
            ? ReadIndirectStrings(fileNameEl, strings)
            : null;

        string?[]? resourceNames = shared.TryGetProperty("resourceTable", out var resourceTable)
            ? ReadIndirectStrings(resourceTable.GetProperty("name"), strings)
            : null;

        int n = name.Length;
        var url = new string[n];
        for (int i = 0; i < n; i++)
        {
            if (sourceIndex is not null && sourceFilenames is not null &&
                sourceIndex[i] is int si && si >= 0 && si < sourceFilenames.Length && sourceFilenames[si] is string sf)
            {
                url[i] = sf;
            }
            else if (legacyFileName is not null && legacyFileName[i] is string lf)
            {
                url[i] = lf;
            }
            else if (resourceNames is not null && resource[i] is int ri && ri >= 0 && ri < resourceNames.Length && resourceNames[ri] is string rn)
            {
                url[i] = rn;
            }
            else
            {
                url[i] = "";
            }
        }

        return new FuncTables(name, url, lineNumber);
    }

    private static FrameTables BuildFrameTables(JsonElement shared)
    {
        var frameTable = shared.GetProperty("frameTable");
        var frameFunc = ReadInts(frameTable.GetProperty("func"));
        var frameLine = ReadNullableInts(frameTable.GetProperty("line"));

        var stackTable = shared.GetProperty("stackTable");
        var stackFrame = ReadInts(stackTable.GetProperty("frame"));

        return new FrameTables(stackFrame, frameFunc, frameLine);
    }

    private static int ParseProcessOrThreadId(JsonElement thread, string propertyName)
    {
        if (!thread.TryGetProperty(propertyName, out var el)) return 0;
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt32(out int v) => v,
            JsonValueKind.String when int.TryParse(el.GetString(), out int v) => v,
            _ => 0,
        };
    }

    // ── low-level array readers ─────────────────────────────────────────────

    private static string[] ReadStrings(JsonElement arr)
    {
        var result = new string[arr.GetArrayLength()];
        int i = 0;
        foreach (var s in arr.EnumerateArray()) result[i++] = s.GetString() ?? "";
        return result;
    }

    private static int[] ReadInts(JsonElement arr)
    {
        var result = new int[arr.GetArrayLength()];
        int i = 0;
        foreach (var v in arr.EnumerateArray())
            result[i++] = v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
        return result;
    }

    private static int?[] ReadNullableInts(JsonElement arr)
    {
        var result = new int?[arr.GetArrayLength()];
        int i = 0;
        foreach (var v in arr.EnumerateArray())
            result[i++] = v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;
        return result;
    }

    private static string?[] ReadIndirectStrings(JsonElement arr, string[] strings)
    {
        var idx = ReadNullableInts(arr);
        var result = new string?[idx.Length];
        for (int i = 0; i < idx.Length; i++)
            result[i] = idx[i] is int k && k >= 0 && k < strings.Length ? strings[k] : null;
        return result;
    }
}
