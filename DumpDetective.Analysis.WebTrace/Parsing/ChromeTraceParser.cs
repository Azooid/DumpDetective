using System.Text.Json;
using System.IO.Compression;
using DumpDetective.Analysis.WebTrace.Model;

namespace DumpDetective.Analysis.WebTrace.Parsing;

/// <summary>
/// Streams a Chrome DevTools "Enhanced Trace" (Trace Event Format JSON, optionally
/// gzip'd) and reduces it directly into <see cref="WebTraceData"/> — the
/// <c>traceEvents</c> array (hundreds of thousands of entries, 200MB+ uncompressed
/// for a typical recording) is never buffered whole. One event is read, inspected,
/// and either folded into an in-flight aggregate or discarded before the next is read.
///
/// Rule (mirrors "single heap walk" for dumps / "single event dispatch" for .NET traces):
/// this is the only place that ever calls <see cref="Utf8JsonReader"/> over the raw
/// file. A new metric means folding another case into <see cref="ParseEvent"/>, not a
/// second pass over the stream.
/// </summary>
public static class ChromeTraceParser
{
    /// <summary>Tasks/spans shorter than this are noise at trace scale — dropped during the single pass, not after.</summary>
    public const long DefaultLongTaskFloorUs = 50_000; // 50ms

    public static WebTraceData Parse(string path, long longTaskFloorUs = DefaultLongTaskFloorUs, Action<string>? progress = null)
    {
        using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16);
        using Stream jsonStream = OpenMaybeGzip(fileStream);
        return Parse(jsonStream, path, longTaskFloorUs, progress);
    }

    /// <summary>Wraps <paramref name="raw"/> in a <see cref="GZipStream"/> if it starts with the gzip magic bytes.</summary>
    private static Stream OpenMaybeGzip(Stream raw)
    {
        Span<byte> magic = stackalloc byte[2];
        int n = raw.Read(magic);
        raw.Position = 0;
        bool isGzip = n == 2 && magic[0] == 0x1F && magic[1] == 0x8B;
        return isGzip ? new GZipStream(raw, CompressionMode.Decompress) : raw;
    }

    public static WebTraceData Parse(Stream jsonStream, string sourcePath, long longTaskFloorUs, Action<string>? progress)
    {
        var cursor = new JsonBufferCursor(jsonStream);
        var reader = cursor.NewReader();

        var counters    = new List<WebCounterSample>();
        var longTasks   = new List<WebLongTask>();
        var gcEvents    = new List<WebGcEvent>();
        var screenshots = new List<WebScreenshotFrame>();
        // Keyed by "url|function|line|column" — bounded by unique call-frame count, not sample count.
        var hotspots    = new Dictionary<string, WebCpuHotspot>(StringComparer.Ordinal);
        // V8 CPU profiler nodes accumulate across ProfileChunk events (a node is only
        // (re)defined the first time it's seen; later chunks reference it by id alone).
        var cpuNodes    = new Dictionary<long, (string Url, string Function, int Line, int Column, long Parent)>();
        // Decoded once from metadata (which always precedes traceEvents in the file), then
        // reused to resolve every hotspot's minified location back to its original source
        // as hotspots are built — no second pass needed.
        var sourceMaps  = new Dictionary<string, DecodedSourceMap>(StringComparer.Ordinal);
        // In-flight network requests, correlated by requestId across Send/Receive/Finish;
        // bounded by concurrent request count, not total requests over the recording.
        var pendingRequests = new Dictionary<string, WebNetworkRequestBuilder>(StringComparer.Ordinal);
        var finishedRequests = new List<WebNetworkRequest>();
        // In-flight InputLatency::* async begin/end pairs, keyed by (id, name); bounded
        // by concurrent unresolved input events, not total input events over the recording.
        var inputLatencyBegins = new Dictionary<string, (long Ts, string Kind)>(StringComparer.Ordinal);
        var inputLatencyEvents = new List<WebInputLatencyEvent>();
        int beginFrameCount = 0, droppedFrameCount = 0;
        // CPU profiler samples only carry a *delta* from the previous sample, not an
        // absolute timestamp — reconstructed here as a running clock per (pid, profiler id)
        // starting from that profiler's "Profile" event startTime, so long-task attribution
        // (below) can ask "what was running at time T" without re-reading the file.
        var profileClockByProfilerId = new Dictionary<string, long>(StringComparer.Ordinal);
        // Self-time per BucketSizeUs window, per hotspot key — bounded by
        // (recording seconds × buckets/sec × unique hotspots per bucket), not sample count.
        var bucketSelfTimeUs = new Dictionary<long, Dictionary<string, long>>();
        // Every sample whose call frame is first-party (application) code, timestamped —
        // used after the pass to find "what first-party code ran shortly before this task"
        // when the task's own attribution can't reach back across a setTimeout/promise
        // boundary. Small in practice: application code is usually a tiny fraction of all
        // samples compared to vendor/library code.
        var applicationSamples = new List<(long Ts, string Key)>();

        long minTs = long.MaxValue, maxTs = long.MinValue;
        long totalEvents = 0;

        if (!TryReadNext(cursor, ref reader)) return Empty(sourcePath); // root StartObject

        while (TryReadNext(cursor, ref reader))
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("metadata"u8))
            {
                TryReadNext(cursor, ref reader);
                sourceMaps = ParseMetadataSourceMaps(cursor, ref reader);
            }
            else if (reader.ValueTextEquals("traceEvents"u8))
            {
                TryReadNext(cursor, ref reader);
                if (reader.TokenType != JsonTokenType.StartArray) { SkipValue(cursor, ref reader); continue; }

                while (true)
                {
                    if (!TryReadNext(cursor, ref reader)) break;
                    if (reader.TokenType == JsonTokenType.EndArray) break;
                    if (reader.TokenType != JsonTokenType.StartObject) continue;

                    totalEvents++;
                    ParseEvent(cursor, ref reader, longTaskFloorUs,
                        counters, longTasks, gcEvents, screenshots, hotspots, cpuNodes, sourceMaps,
                        pendingRequests, finishedRequests, inputLatencyBegins, inputLatencyEvents,
                        profileClockByProfilerId, bucketSelfTimeUs, applicationSamples,
                        ref beginFrameCount, ref droppedFrameCount, ref minTs, ref maxTs);

                    if (progress is not null && totalEvents % 20_000 == 0)
                        progress($"parsed {totalEvents:N0} events...");
                }
            }
            else
            {
                TryReadNext(cursor, ref reader);
                SkipValue(cursor, ref reader);
            }
        }

        AttributeLongTasks(longTasks, bucketSelfTimeUs, hotspots);
        AttributePossibleTriggers(longTasks, applicationSamples, hotspots);
        AttributeInputLatencyBlockers(inputLatencyEvents, longTasks);

        return new WebTraceData
        {
            SourcePath         = sourcePath,
            Counters           = counters,
            CpuHotspots        = hotspots.Values.OrderByDescending(h => h.SelfTimeUs).ToList(),
            LongTasks          = longTasks,
            GcEvents           = gcEvents,
            Screenshots        = screenshots,
            NetworkRequests    = finishedRequests,
            BeginFrameCount    = beginFrameCount,
            DroppedFrameCount  = droppedFrameCount,
            InputLatencyEvents = inputLatencyEvents,
            TotalEventsScanned = totalEvents,
            DurationUs         = maxTs > minTs ? maxTs - minTs : 0,
        };
    }

    private static WebTraceData Empty(string sourcePath) => new()
    {
        SourcePath         = sourcePath,
        Counters           = [],
        CpuHotspots        = [],
        LongTasks          = [],
        GcEvents           = [],
        Screenshots        = [],
        NetworkRequests    = [],
        InputLatencyEvents = [],
    };

    /// <summary>
    /// Parses one <c>traceEvents[i]</c> object (reader positioned at its <c>StartObject</c>,
    /// left positioned at the matching <c>EndObject</c>). Captures only the scalar fields
    /// every event carries plus, when the event's name matches something this parser cares
    /// about, the reduced data from <c>args</c>. <c>args</c> is parsed into a short-lived
    /// <see cref="JsonDocument"/> only because Chrome doesn't guarantee <c>name</c> appears
    /// before <c>args</c> in the object — one small bounded document per event, disposed
    /// before moving to the next.
    /// </summary>
    private static void ParseEvent(
        JsonBufferCursor cursor, ref Utf8JsonReader reader, long longTaskFloorUs,
        List<WebCounterSample> counters, List<WebLongTask> longTasks, List<WebGcEvent> gcEvents,
        List<WebScreenshotFrame> screenshots,
        Dictionary<string, WebCpuHotspot> hotspots,
        Dictionary<long, (string Url, string Function, int Line, int Column, long Parent)> cpuNodes,
        Dictionary<string, DecodedSourceMap> sourceMaps,
        Dictionary<string, WebNetworkRequestBuilder> pendingRequests, List<WebNetworkRequest> finishedRequests,
        Dictionary<string, (long Ts, string Kind)> inputLatencyBegins, List<WebInputLatencyEvent> inputLatencyEvents,
        Dictionary<string, long> profileClockByProfilerId, Dictionary<long, Dictionary<string, long>> bucketSelfTimeUs,
        List<(long Ts, string Key)> applicationSamples,
        ref int beginFrameCount, ref int droppedFrameCount,
        ref long minTs, ref long maxTs)
    {
        WebEventName name = WebEventName.Other;
        long ts = 0, dur = 0;
        int pid = 0, tid = 0;
        char ph = '\0';
        string? id = null;
        string? inputKind = null;
        JsonDocument? argsDoc = null;

        try
        {
            while (TryReadNext(cursor, ref reader) && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                if (reader.ValueTextEquals("name"u8))
                {
                    TryReadNext(cursor, ref reader);
                    name = MatchEventName(ref reader, out inputKind);
                }
                else if (reader.ValueTextEquals("ts"u8))
                {
                    TryReadNext(cursor, ref reader);
                    reader.TryGetInt64(out ts);
                }
                else if (reader.ValueTextEquals("dur"u8))
                {
                    TryReadNext(cursor, ref reader);
                    reader.TryGetInt64(out dur);
                }
                else if (reader.ValueTextEquals("pid"u8))
                {
                    TryReadNext(cursor, ref reader);
                    reader.TryGetInt32(out pid);
                }
                else if (reader.ValueTextEquals("tid"u8))
                {
                    TryReadNext(cursor, ref reader);
                    reader.TryGetInt32(out tid);
                }
                else if (reader.ValueTextEquals("ph"u8))
                {
                    TryReadNext(cursor, ref reader);
                    ph = !reader.HasValueSequence && reader.ValueSpan.Length > 0 ? (char)reader.ValueSpan[0] : '\0';
                }
                else if (reader.ValueTextEquals("id"u8))
                {
                    // Chrome emits "id" as either a string ("0x21") or, rarely, a bare number —
                    // normalize both to a string so async begin/end pairing has one key type.
                    TryReadNext(cursor, ref reader);
                    id = reader.TokenType == JsonTokenType.String
                        ? reader.GetString()
                        : reader.TryGetInt64(out long idNum) ? idNum.ToString() : null;
                }
                else if (reader.ValueTextEquals("args"u8))
                {
                    // JsonDocument.TryParseValue advances the reader itself (it expects to be
                    // positioned on the property name / not-yet-read value) — do not pre-Read here.
                    argsDoc = TryParseValueDocument(cursor, ref reader);
                }
                else
                {
                    TryReadNext(cursor, ref reader);
                    SkipValue(cursor, ref reader);
                }
            }

            if (ts > 0)
            {
                if (ts < minTs) minTs = ts;
                if (ts > maxTs) maxTs = ts;
            }

            switch (name)
            {
                case WebEventName.UpdateCounters when argsDoc is not null:
                    ExtractCounters(argsDoc.RootElement, ts, counters);
                    break;

                case WebEventName.RunTask when dur >= longTaskFloorUs:
                    longTasks.Add(new WebLongTask { TimestampUs = ts, DurationUs = dur, Pid = pid, Tid = tid });
                    break;

                case WebEventName.MajorGc:
                    gcEvents.Add(new WebGcEvent(ts, dur, "Major"));
                    break;

                case WebEventName.MinorGc:
                    gcEvents.Add(new WebGcEvent(ts, dur, "Minor"));
                    break;

                case WebEventName.ProfileStart when argsDoc is not null && id is not null:
                    ExtractProfileStart(argsDoc.RootElement, pid, id, profileClockByProfilerId);
                    break;

                case WebEventName.ProfileChunk when argsDoc is not null && id is not null:
                    ExtractProfileChunk(argsDoc.RootElement, hotspots, cpuNodes, sourceMaps,
                        pid, id, profileClockByProfilerId, bucketSelfTimeUs, applicationSamples);
                    break;

                case WebEventName.Screenshot when argsDoc is not null:
                    ExtractScreenshot(argsDoc.RootElement, ts, screenshots);
                    break;

                case WebEventName.BeginFrame:
                    beginFrameCount++;
                    break;

                case WebEventName.DroppedFrame:
                    droppedFrameCount++;
                    break;

                case WebEventName.InputLatency when id is not null:
                    ExtractInputLatency(ph, id, ts, inputKind, inputLatencyBegins, inputLatencyEvents);
                    break;

                case WebEventName.ResourceSendRequest when argsDoc is not null:
                    ExtractResourceSendRequest(argsDoc.RootElement, ts, pendingRequests);
                    break;

                case WebEventName.ResourceReceiveResponse when argsDoc is not null:
                    ExtractResourceReceiveResponse(argsDoc.RootElement, ts, pendingRequests);
                    break;

                case WebEventName.ResourceFinish when argsDoc is not null:
                    ExtractResourceFinish(argsDoc.RootElement, ts, pendingRequests, finishedRequests);
                    break;
            }
        }
        finally
        {
            argsDoc?.Dispose();
        }
    }

    private static void ExtractInputLatency(
        char ph, string id, long ts, string? inputKind,
        Dictionary<string, (long Ts, string Kind)> begins, List<WebInputLatencyEvent> completed)
    {
        // Async begin/end pairing (Trace Event Format): 'b' opens, 'e' closes, matched by id.
        if (ph == 'b')
        {
            begins[id] = (ts, inputKind ?? "Unknown");
        }
        else if (ph == 'e' && begins.TryGetValue(id, out var begin))
        {
            if (ts > begin.Ts)
                completed.Add(new WebInputLatencyEvent { TimestampUs = begin.Ts, DurationUs = ts - begin.Ts, Kind = begin.Kind });
            begins.Remove(id);
        }
    }

    private static void ExtractResourceSendRequest(
        JsonElement args, long ts, Dictionary<string, WebNetworkRequestBuilder> pending)
    {
        if (!args.TryGetProperty("data", out var data)) return;
        if (!data.TryGetProperty("requestId", out var ridEl)) return;
        string? rid = ridEl.GetString();
        if (string.IsNullOrEmpty(rid)) return;

        if (!pending.TryGetValue(rid, out var b))
            pending[rid] = b = new WebNetworkRequestBuilder();
        b.Url          = data.TryGetProperty("url",            out var u)  ? u.GetString()  ?? "" : "";
        b.Method       = data.TryGetProperty("requestMethod",   out var m)  ? m.GetString()        : null;
        b.ResourceType = data.TryGetProperty("resourceType",    out var rt) ? rt.GetString()       : null;
        b.SendTs       = ts;
    }

    private static void ExtractResourceReceiveResponse(
        JsonElement args, long ts, Dictionary<string, WebNetworkRequestBuilder> pending)
    {
        if (!args.TryGetProperty("data", out var data)) return;
        if (!data.TryGetProperty("requestId", out var ridEl)) return;
        string? rid = ridEl.GetString();
        if (string.IsNullOrEmpty(rid) || !pending.TryGetValue(rid, out var b)) return;

        b.ResponseTs = ts;
        if (data.TryGetProperty("fromCache", out var fc) && fc.ValueKind is JsonValueKind.True or JsonValueKind.False)
            b.FromCache = fc.GetBoolean();
    }

    private static void ExtractResourceFinish(
        JsonElement args, long ts,
        Dictionary<string, WebNetworkRequestBuilder> pending, List<WebNetworkRequest> finished)
    {
        if (!args.TryGetProperty("data", out var data)) return;
        if (!data.TryGetProperty("requestId", out var ridEl)) return;
        string? rid = ridEl.GetString();
        if (string.IsNullOrEmpty(rid)) return;

        pending.TryGetValue(rid, out var b);
        long encoded = data.TryGetProperty("encodedDataLength", out var e) && e.TryGetInt64(out var ev) ? ev : 0;
        long decoded = data.TryGetProperty("decodedBodyLength",  out var d) && d.TryGetInt64(out var dv) ? dv : 0;
        bool failed  = data.TryGetProperty("didFail", out var f) && f.ValueKind is JsonValueKind.True or JsonValueKind.False && f.GetBoolean();

        finished.Add(new WebNetworkRequest
        {
            Url          = b?.Url ?? "",
            Method       = b?.Method,
            ResourceType = b?.ResourceType,
            SendTs       = b?.SendTs ?? 0,
            ResponseTs   = b?.ResponseTs ?? 0,
            FinishTs     = ts,
            EncodedBytes = encoded,
            DecodedBytes = decoded,
            Failed       = failed,
            FromCache    = b?.FromCache ?? false,
        });
        pending.Remove(rid);
    }

    private static void ExtractCounters(JsonElement args, long ts, List<WebCounterSample> counters)
    {
        if (!args.TryGetProperty("data", out var data)) return;
        long heap  = data.TryGetProperty("jsHeapSizeUsed",   out var h) ? h.GetInt64() : 0;
        int docs   = data.TryGetProperty("documents",        out var d) ? d.GetInt32() : 0;
        int nodes  = data.TryGetProperty("nodes",             out var n) ? n.GetInt32() : 0;
        int listen = data.TryGetProperty("jsEventListeners",  out var l) ? l.GetInt32() : 0;
        counters.Add(new WebCounterSample(ts, heap, docs, nodes, listen));
    }

    private static void ExtractScreenshot(JsonElement args, long ts, List<WebScreenshotFrame> screenshots)
    {
        if (!args.TryGetProperty("snapshot", out var snap)) return;
        string? b64 = snap.GetString();
        if (!string.IsNullOrEmpty(b64))
            screenshots.Add(new WebScreenshotFrame(ts, b64));
    }

    /// <summary>Bucket width for the long-task attribution index — small enough to resolve a 50ms task, bounded overall by recording length.</summary>
    private const long BucketSizeUs = 50_000;

    private static void ExtractProfileStart(JsonElement args, int pid, string id, Dictionary<string, long> profileClockByProfilerId)
    {
        if (!args.TryGetProperty("data", out var data)) return;
        if (data.TryGetProperty("startTime", out var st) && st.TryGetInt64(out long startTime))
            profileClockByProfilerId[$"{pid}:{id}"] = startTime;
    }

    private static void ExtractProfileChunk(
        JsonElement args,
        Dictionary<string, WebCpuHotspot> hotspots,
        Dictionary<long, (string Url, string Function, int Line, int Column, long Parent)> cpuNodes,
        Dictionary<string, DecodedSourceMap> sourceMaps,
        int pid, string profilerId,
        Dictionary<string, long> profileClockByProfilerId, Dictionary<long, Dictionary<string, long>> bucketSelfTimeUs,
        List<(long Ts, string Key)> applicationSamples)
    {
        // ProfileChunk wraps its payload in an extra "data" level: args.data.{cpuProfile,timeDeltas}.
        if (!args.TryGetProperty("data", out var data)) return;
        if (!data.TryGetProperty("cpuProfile", out var cpuProfile)) return;

        if (cpuProfile.TryGetProperty("nodes", out var nodesArr))
        {
            foreach (var node in nodesArr.EnumerateArray())
            {
                if (!node.TryGetProperty("id", out var idEl)) continue;
                long id = idEl.GetInt64();
                if (!node.TryGetProperty("callFrame", out var cf)) continue;

                string fn     = cf.TryGetProperty("functionName", out var fnEl) ? fnEl.GetString() ?? "" : "";
                string url    = cf.TryGetProperty("url",          out var urlEl) ? urlEl.GetString() ?? "" : "";
                int    line   = cf.TryGetProperty("lineNumber",   out var lnEl) ? lnEl.GetInt32() : -1;
                int    column = cf.TryGetProperty("columnNumber", out var colEl) ? colEl.GetInt32() : -1;
                long   parent = node.TryGetProperty("parent", out var pEl) && pEl.TryGetInt64(out var pv) ? pv : -1;
                if (fn.Length == 0)
                {
                    fn = cf.TryGetProperty("codeType", out var ctEl) ? $"({ctEl.GetString()})" : "(anonymous)";
                }
                cpuNodes[id] = (url, fn, line, column, parent);
            }
        }

        if (!data.TryGetProperty("timeDeltas", out var timeDeltas) || timeDeltas.ValueKind != JsonValueKind.Array) return;
        if (!cpuProfile.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array) return;

        string profilerKey = $"{pid}:{profilerId}";
        profileClockByProfilerId.TryGetValue(profilerKey, out long clock); // 0 if we never saw this profiler's "Profile" start event

        using var sIter = samples.EnumerateArray();
        using var tIter = timeDeltas.EnumerateArray();
        while (sIter.MoveNext() && tIter.MoveNext())
        {
            long nodeId = sIter.Current.GetInt64();
            long delta  = tIter.Current.GetInt64();
            if (delta <= 0) continue;
            clock += delta; // samples carry a delta from the previous sample, not an absolute ts
            if (!cpuNodes.TryGetValue(nodeId, out var frame)) continue;

            string key = $"{frame.Url}|{frame.Function}|{frame.Line}";
            if (!hotspots.TryGetValue(key, out var h))
            {
                var resolved = Resolve(sourceMaps, frame.Url, frame.Line, frame.Column);
                hotspots[key] = h = new WebCpuHotspot
                {
                    Url          = frame.Url,
                    FunctionName = frame.Function,
                    Line         = frame.Line,
                    ResolvedFile = resolved?.File,
                    ResolvedLine = resolved?.Line ?? -1,
                    CallChain    = BuildCallChain(nodeId, cpuNodes, sourceMaps),
                };
            }
            h.SelfTimeUs  += delta;
            h.SampleCount += 1;

            if (clock > 0)
            {
                long bucket = clock / BucketSizeUs;
                if (!bucketSelfTimeUs.TryGetValue(bucket, out var perHotspot))
                    bucketSelfTimeUs[bucket] = perHotspot = new Dictionary<string, long>(StringComparer.Ordinal);
                perHotspot.TryGetValue(key, out long existing);
                perHotspot[key] = existing + delta;

                if (IsApplicationUrl(frame.Url))
                    applicationSamples.Add((clock, key));
            }
        }
        profileClockByProfilerId[profilerKey] = clock;
    }

    /// <summary>First-party (application) source heuristic — excludes vendor/bundler paths and browser extension content scripts. Mirrors <c>WebCpuHotspotRow.IsApplicationCode</c>.</summary>
    private static bool IsApplicationUrl(string url) =>
        url.Length > 0 &&
        !url.Contains("node_modules", StringComparison.OrdinalIgnoreCase) &&
        !url.Contains("/.vite/deps/", StringComparison.OrdinalIgnoreCase) &&
        !url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase);

    private const int MaxCallChainDepth = 10;

    /// <summary>
    /// Walks the V8 CPU profiler's node.parent chain up from <paramref name="nodeId"/>,
    /// collecting callers — nearest first — until <see cref="MaxCallChainDepth"/> or a
    /// synthetic root frame ("(root)"/"(program)"/"(idle)") is reached. A leaf function's
    /// own name is often a shared utility (e.g. a CSS-property getter called from
    /// everywhere); its immediate callers are what actually reveal which feature is
    /// responsible — computed once per unique hotspot, not per sample.
    ///
    /// Each entry carries its de-minified location and raw URL alongside the function
    /// name (not just the name) — encoded as "FunctionLocationUrl", entries
    /// joined by " ← " — specifically so the report can tell which callers are first-party
    /// (application) code and label them inline, instead of a bare name the reader would
    /// have to separately cross-reference against the "Application Code" list to place.
    /// </summary>
    private static string? BuildCallChain(
        long nodeId, Dictionary<long, (string Url, string Function, int Line, int Column, long Parent)> cpuNodes,
        Dictionary<string, DecodedSourceMap> sourceMaps)
    {
        if (!cpuNodes.TryGetValue(nodeId, out var leaf)) return null;

        var entries = new List<string>(MaxCallChainDepth);
        long parentId = leaf.Parent;
        for (int i = 0; i < MaxCallChainDepth && parentId >= 0; i++)
        {
            if (!cpuNodes.TryGetValue(parentId, out var ancestor)) break;
            if (ancestor.Function is "(root)" or "(program)" or "(idle)") break;

            string fn = ancestor.Function.Length > 0 ? ancestor.Function : "(anonymous)";
            var resolved = Resolve(sourceMaps, ancestor.Url, ancestor.Line, ancestor.Column);
            string location = resolved is not null
                ? $"{resolved.Value.File}:{resolved.Value.Line}"
                : ancestor.Url.Length > 0 ? $"{ancestor.Url}:{ancestor.Line}" : "";
            entries.Add($"{fn}{location}{ancestor.Url}");
            parentId = ancestor.Parent;
        }

        return entries.Count > 0 ? string.Join(" ← ", entries) : null;
    }

    private static (string File, int Line, int Column)? Resolve(
        Dictionary<string, DecodedSourceMap> sourceMaps, string url, int line, int column)
    {
        if (line < 0 || column < 0) return null;
        return sourceMaps.TryGetValue(url, out var map) ? map.Resolve(line, column) : null;
    }

    /// <summary>
    /// Post-pass over already-parsed, bounded in-memory data (not a second file read —
    /// the "single pass" rule is about the raw stream, not about correlating data the
    /// pass already produced): for each long task, sums bucketed self-time across every
    /// bucket the task's window overlaps and attributes it to the top hotspot there.
    /// Approximate at bucket granularity, not exact-to-the-microsecond — good enough to
    /// turn "something ran for 800ms" into "here's what was probably running".
    /// </summary>
    private static void AttributeLongTasks(
        List<WebLongTask> longTasks,
        Dictionary<long, Dictionary<string, long>> bucketSelfTimeUs,
        Dictionary<string, WebCpuHotspot> hotspots)
    {
        if (bucketSelfTimeUs.Count == 0) return;

        foreach (var task in longTasks)
        {
            long firstBucket = task.TimestampUs / BucketSizeUs;
            long lastBucket  = (task.TimestampUs + task.DurationUs) / BucketSizeUs;

            var totals = new Dictionary<string, long>(StringComparer.Ordinal);
            for (long b = firstBucket; b <= lastBucket; b++)
            {
                if (!bucketSelfTimeUs.TryGetValue(b, out var perHotspot)) continue;
                foreach (var (key, us) in perHotspot)
                {
                    totals.TryGetValue(key, out long existing);
                    totals[key] = existing + us;
                }
            }
            if (totals.Count == 0) continue;

            var top = totals.OrderByDescending(kv => kv.Value).First();
            if (!hotspots.TryGetValue(top.Key, out var h)) continue;

            task.AttributedFunction     = h.FunctionName;
            task.AttributedUrl          = h.Url;
            task.AttributedLine         = h.Line;
            task.AttributedResolvedFile = h.ResolvedFile;
            task.AttributedResolvedLine = h.ResolvedLine;
            task.AttributedSelfTimeUs   = top.Value;
            task.AttributedCallChain    = h.CallChain;
        }
    }

    /// <summary>
    /// For each completed input-latency span, finds the long task (if any) whose window
    /// overlaps the interaction's start — i.e. what the main thread was actually busy doing
    /// while the user waited for a response, the same "what was running at time T" answer
    /// web-long-tasks already computes. When more than one task overlaps, picks the longest
    /// (the dominant blocker). Both lists are small (hundreds of entries, not samples-at-scale)
    /// even in a large recording, so a plain O(events × tasks) scan needs no index.
    /// </summary>
    private static void AttributeInputLatencyBlockers(List<WebInputLatencyEvent> events, List<WebLongTask> longTasks)
    {
        if (longTasks.Count == 0 || events.Count == 0) return;

        foreach (var e in events)
        {
            WebLongTask? blocker = null;
            foreach (var t in longTasks)
            {
                if (t.AttributedFunction is null) continue;
                if (t.TimestampUs >= e.TimestampUs + e.DurationUs) continue;
                if (t.TimestampUs + t.DurationUs <= e.TimestampUs) continue;
                if (blocker is null || t.DurationUs > blocker.DurationUs) blocker = t;
            }
            if (blocker is null) continue;

            e.BlockedByFunction     = blocker.AttributedFunction;
            e.BlockedByUrl          = blocker.AttributedUrl;
            e.BlockedByLine         = blocker.AttributedLine;
            e.BlockedByResolvedFile = blocker.AttributedResolvedFile;
            e.BlockedByResolvedLine = blocker.AttributedResolvedLine;
        }
    }

    /// <summary>Only look this far back for a candidate trigger — far enough to catch a debounced setTimeout, not so far it's meaningless.</summary>
    private const long MaxTriggerLookbackUs = 5_000_000; // 5s — wide enough to catch a debounced setTimeout/cascading re-render, not so wide it's meaningless

    /// <summary>
    /// Finds, for each long task, the nearest first-party (application) sample recorded
    /// before it started — a <em>timing correlation</em>, explicitly not a call-tree link,
    /// for the case <see cref="AttributeLongTasks"/> structurally can't cover: a function
    /// that schedules real work via <c>setTimeout</c>/a promise and returns immediately has
    /// no call-tree connection to the deferred work it causes (V8's CPU profiler only
    /// tracks the synchronous stack). Runs over the already-parsed, bounded
    /// <paramref name="applicationSamples"/> list — not a second file read.
    /// </summary>
    private static void AttributePossibleTriggers(
        List<WebLongTask> longTasks, List<(long Ts, string Key)> applicationSamples,
        Dictionary<string, WebCpuHotspot> hotspots)
    {
        if (applicationSamples.Count == 0) return;

        applicationSamples.Sort(static (a, b) => a.Ts.CompareTo(b.Ts));
        var timestamps = new long[applicationSamples.Count];
        for (int i = 0; i < applicationSamples.Count; i++) timestamps[i] = applicationSamples[i].Ts;

        foreach (var task in longTasks)
        {
            int idx = Array.BinarySearch(timestamps, task.TimestampUs);
            if (idx < 0) idx = ~idx - 1; // insertion point — step back to the last sample at/before task start
            if (idx < 0) continue;

            var (ts, key) = applicationSamples[idx];
            long gap = task.TimestampUs - ts;
            if (gap < 0 || gap > MaxTriggerLookbackUs) continue;
            if (!hotspots.TryGetValue(key, out var h)) continue;

            task.PossibleTriggerFunction     = h.FunctionName;
            task.PossibleTriggerUrl          = h.Url;
            task.PossibleTriggerLine         = h.Line;
            task.PossibleTriggerResolvedFile = h.ResolvedFile;
            task.PossibleTriggerResolvedLine = h.ResolvedLine;
            task.PossibleTriggerGapUs        = gap;

            // The single nearest sample can be misleadingly stale (reused across many
            // later tasks once nothing closer exists) and, when several first-party
            // functions fire together as a setup/render burst, picking just one hides the
            // rest. Separately collect every distinct first-party function seen in the
            // tight window right before this task — a burst shows up here as a group.
            task.PossibleTriggerCluster = BuildTriggerCluster(applicationSamples, timestamps, idx, task.TimestampUs, hotspots);
        }
    }

    private const long TriggerClusterWindowUs = 1_000_000; // 1s — tighter than the single-nearest lookback, deliberately
    private const int  MaxTriggerClusterEntries = 5;

    private static string? BuildTriggerCluster(
        List<(long Ts, string Key)> applicationSamples, long[] timestamps, int nearestIdx, long taskStartUs,
        Dictionary<string, WebCpuHotspot> hotspots)
    {
        long windowStart = taskStartUs - TriggerClusterWindowUs;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<string>();

        for (int i = nearestIdx; i >= 0 && entries.Count < MaxTriggerClusterEntries; i--)
        {
            var (ts, key) = applicationSamples[i];
            if (ts < windowStart) break; // sorted ascending — everything further back is outside the window too
            if (!seen.Add(key)) continue;
            if (!hotspots.TryGetValue(key, out var h)) continue;

            string location = h.ResolvedFile is not null
                ? $"{h.ResolvedFile}:{h.ResolvedLine}"
                : h.Url.Length > 0 ? $"{h.Url}:{h.Line}" : "(native)";
            entries.Add($"{h.FunctionName} ({location})");
        }

        return entries.Count > 0 ? string.Join(", ", entries) : null;
    }

    // ── metadata.sourceMaps parsing ─────────────────────────────────────────
    // Deliberately hand-walked (not JsonDocument.ParseValue) so the large
    // "sourcesContent" field — the full original, unminified source text, never
    // needed for analysis — can be skipped without ever being materialized.

    private static Dictionary<string, DecodedSourceMap> ParseMetadataSourceMaps(JsonBufferCursor cursor, ref Utf8JsonReader reader)
    {
        var result = new Dictionary<string, DecodedSourceMap>(StringComparer.Ordinal);
        if (reader.TokenType != JsonTokenType.StartObject) { SkipValue(cursor, ref reader); return result; }

        while (TryReadNext(cursor, ref reader) && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("sourceMaps"u8))
            {
                TryReadNext(cursor, ref reader);
                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    while (TryReadNext(cursor, ref reader) && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType != JsonTokenType.StartObject) continue;
                        ParseOneSourceMapEntry(cursor, ref reader, result);
                    }
                }
                else
                {
                    SkipValue(cursor, ref reader);
                }
            }
            else
            {
                TryReadNext(cursor, ref reader);
                SkipValue(cursor, ref reader);
            }
        }
        return result;
    }

    private static void ParseOneSourceMapEntry(
        JsonBufferCursor cursor, ref Utf8JsonReader reader, Dictionary<string, DecodedSourceMap> result)
    {
        string?  url      = null;
        string[]? sources = null;
        string?  mappings = null;

        while (TryReadNext(cursor, ref reader) && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("url"u8))
            {
                TryReadNext(cursor, ref reader);
                url = reader.GetString();
            }
            else if (reader.ValueTextEquals("sourceMap"u8))
            {
                TryReadNext(cursor, ref reader);
                (sources, mappings) = ParseSourceMapObject(cursor, ref reader);
            }
            else
            {
                TryReadNext(cursor, ref reader);
                SkipValue(cursor, ref reader);
            }
        }

        if (url is not null && sources is not null && mappings is not null)
            result[url] = SourceMapVlq.Decode(mappings, sources);
    }

    private static (string[]? Sources, string? Mappings) ParseSourceMapObject(JsonBufferCursor cursor, ref Utf8JsonReader reader)
    {
        string[]? sources = null;
        string?   mappings = null;
        if (reader.TokenType != JsonTokenType.StartObject) { SkipValue(cursor, ref reader); return (null, null); }

        while (TryReadNext(cursor, ref reader) && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("sources"u8))
            {
                TryReadNext(cursor, ref reader);
                sources = ParseStringArray(cursor, ref reader);
            }
            else if (reader.ValueTextEquals("mappings"u8))
            {
                TryReadNext(cursor, ref reader);
                mappings = reader.GetString();
            }
            else
            {
                // Skips "sourcesContent" here (and "names", not currently used) —
                // the full original source text, never needed for hotspot resolution.
                TryReadNext(cursor, ref reader);
                SkipValue(cursor, ref reader);
            }
        }
        return (sources, mappings);
    }

    private static string[] ParseStringArray(JsonBufferCursor cursor, ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) { SkipValue(cursor, ref reader); return []; }
        var list = new List<string>();
        while (TryReadNext(cursor, ref reader) && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String) list.Add(reader.GetString() ?? "");
        }
        return [.. list];
    }

    // ── low-level token plumbing ────────────────────────────────────────────

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

    /// <summary>Parses the value the reader is currently positioned at into a standalone document, growing the buffer as needed.</summary>
    private static JsonDocument? TryParseValueDocument(JsonBufferCursor cursor, ref Utf8JsonReader reader)
    {
        while (true)
        {
            if (JsonDocument.TryParseValue(ref reader, out var doc)) return doc;
            if (reader.IsFinalBlock) return null;
            cursor.Advance(reader);
            reader = cursor.NewReader();
        }
    }

    private enum WebEventName
    {
        Other, UpdateCounters, RunTask, ProfileStart, ProfileChunk, MajorGc, MinorGc, Screenshot,
        BeginFrame, DroppedFrame, InputLatency,
        ResourceSendRequest, ResourceReceiveResponse, ResourceFinish,
    }

    private static readonly byte[] InputLatencyPrefix = "InputLatency::"u8.ToArray();

    private static WebEventName MatchEventName(ref Utf8JsonReader reader, out string? inputKind)
    {
        inputKind = null;
        if (reader.ValueTextEquals("UpdateCounters"u8))          return WebEventName.UpdateCounters;
        if (reader.ValueTextEquals("RunTask"u8))                 return WebEventName.RunTask;
        if (reader.ValueTextEquals("Profile"u8))                 return WebEventName.ProfileStart;
        if (reader.ValueTextEquals("ProfileChunk"u8))            return WebEventName.ProfileChunk;
        if (reader.ValueTextEquals("V8.GC_MARK_COMPACTOR"u8))    return WebEventName.MajorGc;
        if (reader.ValueTextEquals("V8.GC_SCAVENGER"u8))         return WebEventName.MinorGc;
        if (reader.ValueTextEquals("Screenshot"u8))              return WebEventName.Screenshot;
        if (reader.ValueTextEquals("BeginFrame"u8))              return WebEventName.BeginFrame;
        if (reader.ValueTextEquals("DroppedFrame"u8))            return WebEventName.DroppedFrame;
        if (reader.ValueTextEquals("ResourceSendRequest"u8))     return WebEventName.ResourceSendRequest;
        if (reader.ValueTextEquals("ResourceReceiveResponse"u8)) return WebEventName.ResourceReceiveResponse;
        if (reader.ValueTextEquals("ResourceFinish"u8))          return WebEventName.ResourceFinish;
        // All "InputLatency::MouseMove" / "InputLatency::GestureScrollUpdate" / etc variants —
        // matched by prefix on the raw UTF-8 bytes rather than a per-variant whitelist, since
        // Chrome emits many of these and the specific kind doesn't change how we aggregate them —
        // but the suffix (the actual interaction kind) is still worth keeping around for display,
        // so a report doesn't reduce every interaction to an anonymous duration.
        if (!reader.HasValueSequence && reader.ValueSpan.StartsWith(InputLatencyPrefix))
        {
            inputKind = System.Text.Encoding.UTF8.GetString(reader.ValueSpan[InputLatencyPrefix.Length..]);
            return WebEventName.InputLatency;
        }
        return WebEventName.Other;
    }
}
