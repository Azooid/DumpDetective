using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;


namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses System.Net.Sockets EventSource events to detect socket exhaustion,
/// slow connections, and connection failures.
/// Provider: System.Net.Sockets — available in .nettrace and ETL.
/// </summary>
public sealed class SocketTraceAnalyzer
{
    private const double SlowConnectMs = 500.0;

    public SocketTraceData Analyze(string tracePath, int top = 20, string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new SocketTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, 0, [], [], null, false);
        }
    }

    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly Dictionary<int, (double StartMs, string Endpoint)> Pending = new();
        internal readonly Dictionary<string, HostAcc> ByHost = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<SocketConnectEntry> SlowConnects = new();
        internal readonly Dictionary<int, int> PerSecond = new();
        internal int Connects, Failures;
        internal double TotalMs, MaxMs;

        public void Consume(TraceEvent ev, string evName, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            bool isConnStart =
                evName.Contains("Socket") &&
                evName.Contains("Connect") &&
                (evName.EndsWith("Start",  StringComparison.OrdinalIgnoreCase) ||
                 evName.EndsWith("Begin",  StringComparison.OrdinalIgnoreCase));
            bool isConnStop =
                evName.Contains("Socket") &&
                evName.Contains("Connect") &&
                (evName.EndsWith("Stop",   StringComparison.OrdinalIgnoreCase) ||
                 evName.EndsWith("End",    StringComparison.OrdinalIgnoreCase));
            bool isConnFail =
                evName.Contains("Socket") &&
                (evName.Contains("ConnectFailed", StringComparison.OrdinalIgnoreCase) ||
                 evName.Contains("Error",         StringComparison.OrdinalIgnoreCase));

            if (!isConnStart && !isConnStop && !isConnFail) return;

            string endpoint = SafeStr(ev, "Address");
            if (endpoint.Length == 0) endpoint = SafeStr(ev, "RemoteEndPoint");
            if (endpoint.Length == 0) endpoint = SafeStr(ev, "Host");
            if (endpoint.Length == 0) endpoint = "(unknown)";

            string host = endpoint.Contains(':') ? endpoint[..endpoint.LastIndexOf(':')] : endpoint;

            if (isConnStart)
            {
                Pending[threadId] = (timestampMs, endpoint);
                int bucket = (int)(timestampMs / 1000.0);
                PerSecond.TryGetValue(bucket, out int pv);
                PerSecond[bucket] = pv + 1;
            }
            else if (isConnStop && Pending.TryGetValue(threadId, out var start))
            {
                Pending.Remove(threadId);
                double ms = timestampMs - start.StartMs;
                Connects++;
                TotalMs += ms;
                if (ms > MaxMs) MaxMs = ms;

                if (!ByHost.TryGetValue(host, out var acc))
                    ByHost[host] = acc = new HostAcc();
                acc.Count++;
                acc.TotalMs += ms;

                if (ms >= SlowConnectMs)
                    SlowConnects.Add(new SocketConnectEntry(start.Endpoint, ms, false,
                        start.StartMs));
            }
            else if (isConnFail)
            {
                Failures++;
                if (!ByHost.TryGetValue(host, out var acc))
                    ByHost[host] = acc = new HostAcc();
                acc.Failures++;
                SlowConnects.Add(new SocketConnectEntry(endpoint, 0, true,
                    timestampMs));
            }
        }

        public bool WantsEvent(string eventName) => eventName.Contains("Socket", StringComparison.OrdinalIgnoreCase);

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null)
        => new Consumer(processFilter);

    public SocketTraceData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 20,
                                        string? processFilter = null)
    {
        var c = (Consumer)consumer;
        if (c.Connects == 0 && c.Failures == 0)
        {
            return new SocketTraceData(
                $"{traceFileName}  |  0 socket events — collect with " +
                "--providers 'System.Net.Sockets:0xFF:5'",
                processFilter, 0, 0, 0, 0, 0, [], [], null, false);
        }

        double avg = c.Connects > 0 ? c.TotalMs / c.Connects : 0;

        var topHosts = c.ByHost
            .OrderByDescending(kv => kv.Value.Count)
            .Take(top)
            .Select(kv => new SocketHostSummary(kv.Key, kv.Value.Count, kv.Value.Failures,
                kv.Value.TotalMs, kv.Value.Count > 0 ? kv.Value.TotalMs / kv.Value.Count : 0))
            .ToList();

        var topSlow = c.SlowConnects.OrderByDescending(s => s.DurationMs).Take(top).ToList();

        var timeline = BuildTimeline(c.PerSecond);

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {c.Connects:N0} connects  •  {c.Failures} failures  •  avg {avg:F1} ms";

        return new SocketTraceData(info, processFilter,
            c.Connects, c.Failures, avg, c.MaxMs, topSlow.Count,
            topSlow, topHosts, timeline, HasData: true);
    }

    public SocketTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
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
            return new SocketTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, 0, [], [], null, false);
        }
    }


    private sealed class HostAcc { public int Count, Failures; public double TotalMs; }
}
