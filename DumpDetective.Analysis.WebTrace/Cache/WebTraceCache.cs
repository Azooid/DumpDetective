using DumpDetective.Analysis.WebTrace.Model;

namespace DumpDetective.Analysis.WebTrace.Cache;

/// <summary>
/// Disk cache for parsed <see cref="WebTraceData"/>, same shape as the existing
/// <c>.ddcache/&lt;dump-name&gt;/</c> layer documented in <c>Docs/cache.md</c>: build once,
/// validate against the source file's size + last-write-time on every later run, silently
/// rebuild on mismatch. A Chrome trace's 200MB+ JSON only ever needs to be streamed once —
/// every command after that reads this file instead.
///
/// v1 keeps everything the parser reduces (counters / CPU hotspots / long tasks / GC
/// events) in one compact binary blob rather than the five separate per-analyzer files
/// sketched in the design doc — simpler for now, still gets the "never re-parse the raw
/// file" win, and splitting later (e.g. so a source-map cache can be rebuilt independently)
/// is a pure implementation detail that doesn't change any command's public surface.
/// </summary>
public static class WebTraceCache
{
    private const uint   Magic       = 0x44445754; // "DDWT"
    private const int    Version     = 12; // v12: input-latency events now carry kind + long-task blocker attribution, not just a bare duration

    public static string CachePathFor(string tracePath)
    {
        var dir      = Path.GetDirectoryName(Path.GetFullPath(tracePath)) ?? ".";
        var name     = Path.GetFileName(tracePath);
        var cacheDir = Path.Combine(dir, ".ddcache", name);
        Directory.CreateDirectory(cacheDir);
        return Path.Combine(cacheDir, "web.cache");
    }

    public static WebTraceData? TryLoad(string tracePath)
    {
        string cachePath = CachePathFor(tracePath);
        if (!File.Exists(cachePath)) return null;

        var srcInfo = new FileInfo(tracePath);
        if (!srcInfo.Exists) return null;

        try
        {
            using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);

            if (br.ReadUInt32() != Magic) return null;
            if (br.ReadInt32()  != Version) return null;
            long cachedLength = br.ReadInt64();
            long cachedTicks  = br.ReadInt64();
            if (cachedLength != srcInfo.Length || cachedTicks != srcInfo.LastWriteTimeUtc.Ticks)
                return null; // stale — source file changed since this cache was built

            return ReadBody(br, tracePath);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or FormatException)
        {
            return null; // corrupt/partial cache — caller re-parses and overwrites it
        }
    }

    public static void Save(string tracePath, WebTraceData data)
    {
        string cachePath = CachePathFor(tracePath);
        var srcInfo = new FileInfo(tracePath);

        string tmpPath = cachePath + ".tmp";
        using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(Magic);
            bw.Write(Version);
            bw.Write(srcInfo.Length);
            bw.Write(srcInfo.LastWriteTimeUtc.Ticks);
            WriteBody(bw, data);
        }
        File.Move(tmpPath, cachePath, overwrite: true);
    }

    private static void WriteBody(BinaryWriter bw, WebTraceData data)
    {
        bw.Write(data.TotalEventsScanned);
        bw.Write(data.DurationUs);

        bw.Write(data.Counters.Count);
        foreach (var c in data.Counters)
        {
            bw.Write(c.TimestampUs);
            bw.Write(c.JsHeapSizeUsed);
            bw.Write(c.Documents);
            bw.Write(c.Nodes);
            bw.Write(c.JsEventListeners);
        }

        bw.Write(data.CpuHotspots.Count);
        foreach (var h in data.CpuHotspots)
        {
            bw.Write(h.Url);
            bw.Write(h.FunctionName);
            bw.Write(h.Line);
            bw.Write(h.SelfTimeUs);
            bw.Write(h.SampleCount);
            bw.Write(h.ResolvedFile is not null);
            if (h.ResolvedFile is not null)
            {
                bw.Write(h.ResolvedFile);
                bw.Write(h.ResolvedLine);
            }
            bw.Write(h.CallChain is not null);
            if (h.CallChain is not null) bw.Write(h.CallChain);
        }

        bw.Write(data.LongTasks.Count);
        foreach (var t in data.LongTasks)
        {
            bw.Write(t.TimestampUs);
            bw.Write(t.DurationUs);
            bw.Write(t.Pid);
            bw.Write(t.Tid);
            bw.Write(t.AttributedFunction is not null);
            if (t.AttributedFunction is not null)
            {
                bw.Write(t.AttributedFunction);
                bw.Write(t.AttributedUrl ?? "");
                bw.Write(t.AttributedLine);
                bw.Write(t.AttributedResolvedFile is not null);
                if (t.AttributedResolvedFile is not null)
                {
                    bw.Write(t.AttributedResolvedFile);
                    bw.Write(t.AttributedResolvedLine);
                }
                bw.Write(t.AttributedSelfTimeUs);
                bw.Write(t.AttributedCallChain is not null);
                if (t.AttributedCallChain is not null) bw.Write(t.AttributedCallChain);
            }
            bw.Write(t.PossibleTriggerFunction is not null);
            if (t.PossibleTriggerFunction is not null)
            {
                bw.Write(t.PossibleTriggerFunction);
                bw.Write(t.PossibleTriggerUrl ?? "");
                bw.Write(t.PossibleTriggerLine);
                bw.Write(t.PossibleTriggerResolvedFile is not null);
                if (t.PossibleTriggerResolvedFile is not null)
                {
                    bw.Write(t.PossibleTriggerResolvedFile);
                    bw.Write(t.PossibleTriggerResolvedLine);
                }
                bw.Write(t.PossibleTriggerGapUs);
            }
            bw.Write(t.PossibleTriggerCluster is not null);
            if (t.PossibleTriggerCluster is not null) bw.Write(t.PossibleTriggerCluster);
        }

        bw.Write(data.GcEvents.Count);
        foreach (var g in data.GcEvents)
        {
            bw.Write(g.TimestampUs);
            bw.Write(g.DurationUs);
            bw.Write(g.Kind);
        }

        bw.Write(data.Screenshots.Count);
        foreach (var s in data.Screenshots)
        {
            bw.Write(s.TimestampUs);
            bw.Write(s.Base64Jpeg);
        }

        bw.Write(data.NetworkRequests.Count);
        foreach (var r in data.NetworkRequests)
        {
            bw.Write(r.Url);
            bw.Write(r.Method ?? "");
            bw.Write(r.ResourceType ?? "");
            bw.Write(r.SendTs);
            bw.Write(r.ResponseTs);
            bw.Write(r.FinishTs);
            bw.Write(r.EncodedBytes);
            bw.Write(r.DecodedBytes);
            bw.Write(r.Failed);
            bw.Write(r.FromCache);
        }

        bw.Write(data.BeginFrameCount);
        bw.Write(data.DroppedFrameCount);

        bw.Write(data.InputLatencyEvents.Count);
        foreach (var e in data.InputLatencyEvents)
        {
            bw.Write(e.TimestampUs);
            bw.Write(e.DurationUs);
            bw.Write(e.Kind);
            bw.Write(e.BlockedByFunction is not null);
            if (e.BlockedByFunction is not null)
            {
                bw.Write(e.BlockedByFunction);
                bw.Write(e.BlockedByUrl ?? "");
                bw.Write(e.BlockedByLine);
                bw.Write(e.BlockedByResolvedFile is not null);
                if (e.BlockedByResolvedFile is not null)
                {
                    bw.Write(e.BlockedByResolvedFile);
                    bw.Write(e.BlockedByResolvedLine);
                }
            }
        }
    }

    private static WebTraceData ReadBody(BinaryReader br, string tracePath)
    {
        long totalEvents = br.ReadInt64();
        long durationUs  = br.ReadInt64();

        int counterCount = br.ReadInt32();
        var counters = new List<WebCounterSample>(counterCount);
        for (int i = 0; i < counterCount; i++)
            counters.Add(new WebCounterSample(
                br.ReadInt64(), br.ReadInt64(), br.ReadInt32(), br.ReadInt32(), br.ReadInt32()));

        int hotspotCount = br.ReadInt32();
        var hotspots = new List<WebCpuHotspot>(hotspotCount);
        for (int i = 0; i < hotspotCount; i++)
        {
            string url = br.ReadString(), fn = br.ReadString();
            int line = br.ReadInt32();
            long selfTimeUs = br.ReadInt64(), sampleCount = br.ReadInt64();
            bool hasResolved = br.ReadBoolean();
            string? resolvedFile = null;
            int resolvedLine = -1;
            if (hasResolved) { resolvedFile = br.ReadString(); resolvedLine = br.ReadInt32(); }
            string? callChain = br.ReadBoolean() ? br.ReadString() : null;
            hotspots.Add(new WebCpuHotspot
            {
                Url          = url,
                FunctionName = fn,
                Line         = line,
                SelfTimeUs   = selfTimeUs,
                SampleCount  = sampleCount,
                ResolvedFile = resolvedFile,
                ResolvedLine = resolvedLine,
                CallChain    = callChain,
            });
        }

        int taskCount = br.ReadInt32();
        var tasks = new List<WebLongTask>(taskCount);
        for (int i = 0; i < taskCount; i++)
        {
            var task = new WebLongTask
            {
                TimestampUs = br.ReadInt64(),
                DurationUs  = br.ReadInt64(),
                Pid         = br.ReadInt32(),
                Tid         = br.ReadInt32(),
            };
            if (br.ReadBoolean())
            {
                task.AttributedFunction = br.ReadString();
                task.AttributedUrl      = br.ReadString();
                task.AttributedLine     = br.ReadInt32();
                if (br.ReadBoolean())
                {
                    task.AttributedResolvedFile = br.ReadString();
                    task.AttributedResolvedLine = br.ReadInt32();
                }
                task.AttributedSelfTimeUs = br.ReadInt64();
                task.AttributedCallChain  = br.ReadBoolean() ? br.ReadString() : null;
            }
            if (br.ReadBoolean())
            {
                task.PossibleTriggerFunction = br.ReadString();
                task.PossibleTriggerUrl      = br.ReadString();
                task.PossibleTriggerLine     = br.ReadInt32();
                if (br.ReadBoolean())
                {
                    task.PossibleTriggerResolvedFile = br.ReadString();
                    task.PossibleTriggerResolvedLine = br.ReadInt32();
                }
                task.PossibleTriggerGapUs = br.ReadInt64();
            }
            task.PossibleTriggerCluster = br.ReadBoolean() ? br.ReadString() : null;
            tasks.Add(task);
        }

        int gcCount = br.ReadInt32();
        var gcEvents = new List<WebGcEvent>(gcCount);
        for (int i = 0; i < gcCount; i++)
            gcEvents.Add(new WebGcEvent(br.ReadInt64(), br.ReadInt64(), br.ReadString()));

        int shotCount = br.ReadInt32();
        var screenshots = new List<WebScreenshotFrame>(shotCount);
        for (int i = 0; i < shotCount; i++)
            screenshots.Add(new WebScreenshotFrame(br.ReadInt64(), br.ReadString()));

        int reqCount = br.ReadInt32();
        var requests = new List<WebNetworkRequest>(reqCount);
        for (int i = 0; i < reqCount; i++)
        {
            string url = br.ReadString();
            string method = br.ReadString(), resourceType = br.ReadString();
            long sendTs = br.ReadInt64(), responseTs = br.ReadInt64(), finishTs = br.ReadInt64();
            long encoded = br.ReadInt64(), decoded = br.ReadInt64();
            bool failed = br.ReadBoolean(), fromCache = br.ReadBoolean();
            requests.Add(new WebNetworkRequest
            {
                Url          = url,
                Method       = method.Length > 0 ? method : null,
                ResourceType = resourceType.Length > 0 ? resourceType : null,
                SendTs       = sendTs,
                ResponseTs   = responseTs,
                FinishTs     = finishTs,
                EncodedBytes = encoded,
                DecodedBytes = decoded,
                Failed       = failed,
                FromCache    = fromCache,
            });
        }

        int beginFrameCount = br.ReadInt32();
        int droppedFrameCount = br.ReadInt32();

        int latencyCount = br.ReadInt32();
        var latencyEvents = new List<WebInputLatencyEvent>(latencyCount);
        for (int i = 0; i < latencyCount; i++)
        {
            long ts = br.ReadInt64(), dur = br.ReadInt64();
            string kind = br.ReadString();
            var e = new WebInputLatencyEvent { TimestampUs = ts, DurationUs = dur, Kind = kind };
            if (br.ReadBoolean())
            {
                e.BlockedByFunction = br.ReadString();
                e.BlockedByUrl      = br.ReadString();
                e.BlockedByLine     = br.ReadInt32();
                if (br.ReadBoolean())
                {
                    e.BlockedByResolvedFile = br.ReadString();
                    e.BlockedByResolvedLine = br.ReadInt32();
                }
            }
            latencyEvents.Add(e);
        }

        return new WebTraceData
        {
            SourcePath         = tracePath,
            Counters           = counters,
            CpuHotspots        = hotspots,
            LongTasks          = tasks,
            GcEvents           = gcEvents,
            Screenshots        = screenshots,
            NetworkRequests    = requests,
            BeginFrameCount    = beginFrameCount,
            DroppedFrameCount  = droppedFrameCount,
            InputLatencyEvents = latencyEvents,
            TotalEventsScanned = totalEvents,
            DurationUs         = durationUs,
        };
    }
}
