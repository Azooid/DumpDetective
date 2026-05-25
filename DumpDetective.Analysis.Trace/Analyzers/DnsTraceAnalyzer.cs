using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using static DumpDetective.Core.Tracing.TraceEventKind;


namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses System.Net.NameResolution EventSource events to detect slow DNS lookups,
/// repeated resolutions (cache misses), and resolution failures.
/// Provider: System.Net.NameResolution — available in .nettrace and ETL.
/// </summary>
public sealed class DnsTraceAnalyzer
{
    private const double SlowDnsMs = 100.0;

    public DnsTraceData Analyze(string tracePath, int top = 20, string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new DnsTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, [], [], null, false);
        }
    }

    private static readonly Dictionary<string, byte> EvKind = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly Dictionary<int, (double StartMs, string Host)> Pending = new();
        internal readonly Dictionary<string, HostAcc> ByHost = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<DnsSlowResolution> SlowList = new();
        internal readonly Dictionary<int, int> FailTimeline = new();
        internal int Total;
        internal int Failed;
        internal double TotalMs;
        internal double MaxMs;

        public void Consume(TraceEvent ev, in TraceEventMeta meta, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            if (!EvKind.TryGetValue(meta.EventName, out byte kind))
                EvKind[meta.EventName] = kind = ComputeDnsKind(meta.EventName);
            if (kind == 0) return;

            string host = SafeStr(ev, "HostName");
            if (host.Length == 0) host = SafeStr(ev, "Host");
            if (host.Length == 0) host = SafeStr(ev, "Name");
            if (host.Length == 0) host = "(unknown)";

            if (kind == 1) // start
            {
                Pending[threadId] = (timestampMs, host);
            }
            else if (kind == 2 && Pending.TryGetValue(threadId, out var start)) // stop
            {
                Pending.Remove(threadId);
                string resolvedHost = host.Length > 1 ? host : start.Host;
                double ms = timestampMs - start.StartMs;
                Total++;
                TotalMs += ms;
                if (ms > MaxMs) MaxMs = ms;

                if (!ByHost.TryGetValue(resolvedHost, out var acc))
                    ByHost[resolvedHost] = acc = new HostAcc();
                acc.Count++;
                acc.TotalMs += ms;
                if (ms > acc.MaxMs) acc.MaxMs = ms;

                if (ms >= SlowDnsMs)
                    SlowList.Add(new DnsSlowResolution(resolvedHost, ms, false,
                        start.StartMs));
            }
            else if (kind == 3) // fail
            {
                Failed++;
                if (!ByHost.TryGetValue(host, out var acc))
                    ByHost[host] = acc = new HostAcc();
                acc.Failures++;
                SlowList.Add(new DnsSlowResolution(host, 0, true,
                    timestampMs));

                int bucket = (int)(timestampMs / 1000.0);
                FailTimeline.TryGetValue(bucket, out int pv);
                FailTimeline[bucket] = pv + 1;
            }
        }

        public bool WantsEvent(in TraceEventMeta meta)
        {
            if (!EvKind.TryGetValue(meta.EventName, out byte v))
                EvKind[meta.EventName] = v = meta.Kind switch
                {
                    _ when meta.Kind == DnsResolutionStart => 1,
                    _ when meta.Kind == DnsResolutionStop => 2,
                    _ when meta.Kind == DnsResolutionFailed => 3,
                    _ when meta.IsKnown => 0,
                    _ => ComputeDnsKind(meta.EventName)
                };
            return v != 0;
        }

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null)
        => new Consumer(processFilter);

    public DnsTraceData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 20,
                                     string? processFilter = null)
    {
        var c = (Consumer)consumer;
        if (c.Total == 0 && c.Failed == 0)
        {
            return new DnsTraceData(
                $"{traceFileName}  |  0 DNS events — collect with " +
                "--providers 'System.Net.NameResolution:0xFF:5'",
                processFilter, 0, 0, 0, 0, [], [], null, false);
        }

        double avg = c.Total > 0 ? c.TotalMs / c.Total : 0;

        var topHosts = c.ByHost
            .OrderByDescending(kv => kv.Value.Count)
            .Take(top)
            .Select(kv => new DnsHostSummary(kv.Key, kv.Value.Count, kv.Value.Failures,
                kv.Value.TotalMs, kv.Value.Count > 0 ? kv.Value.TotalMs / kv.Value.Count : 0,
                kv.Value.MaxMs))
            .ToList();

        var topSlow = c.SlowList.OrderByDescending(s => s.DurationMs).Take(top).ToList();

        var timeline = BuildTimeline(c.FailTimeline);

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {c.Total:N0} resolutions  •  {c.Failed} failures  •  avg {avg:F1} ms";

        return new DnsTraceData(info, processFilter,
            c.Total, c.Failed, avg, c.MaxMs, topHosts, topSlow, timeline, HasData: true);
    }

    public DnsTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                 string? processFilter = null, Action<string>? progress = null)
    {
        try
        {
            var c = CreateConsumer(processFilter);
            TraceEventDispatcher.Dispatch(trace, c,
                progress);
            return BuildResult(c, traceFileName, top, processFilter);
        }
        catch (Exception ex)
        {
            return new DnsTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, [], [], null, false);
        }
    }

    /// <summary>Classify event name once: 0=skip, 1=start, 2=stop, 3=fail.</summary>
    private static byte ComputeDnsKind(string n)
    {
        bool isDns = n.Contains("NameResolution", StringComparison.OrdinalIgnoreCase) ||
                     n.Contains("DnsResolut",     StringComparison.OrdinalIgnoreCase) ||
                     n.Contains("GetHostEntry",   StringComparison.OrdinalIgnoreCase);
        if (!isDns) return 0;
        if (n.Contains("Fail",  StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Error", StringComparison.OrdinalIgnoreCase)) return 3;
        if (n.EndsWith("Start", StringComparison.OrdinalIgnoreCase) ||
            n.EndsWith("Begin", StringComparison.OrdinalIgnoreCase)) return 1;
        if (n.EndsWith("Stop",  StringComparison.OrdinalIgnoreCase) ||
            n.EndsWith("End",   StringComparison.OrdinalIgnoreCase)) return 2;
        return 0;
    }


    private sealed class HostAcc { public int Count, Failures; public double TotalMs, MaxMs; }
}
