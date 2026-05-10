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

    public SocketTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                    string? processFilter = null)
    {
        var pending = new Dictionary<int, (double StartMs, string Endpoint)>();
        var byHost  = new Dictionary<string, HostAcc>(StringComparer.OrdinalIgnoreCase);
        var slowConnects = new List<SocketConnectEntry>();
        var perSecond    = new Dictionary<int, int>();

        int connects = 0, failures = 0;
        double totalMs = 0, maxMs = 0;

        try
        {
            foreach (var ev in trace.Events)
            {
                if (processFilter is not null &&
                    !ev.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";
                bool isConnStart =
                    evName.Contains("Socket") &&
                    evName.Contains("Connect") &&
                    (evName.EndsWith("Start",  StringComparison.OrdinalIgnoreCase) ||
                     evName.EndsWith("Begin",  StringComparison.OrdinalIgnoreCase));
                bool isConnStop =
                    evName.Contains("Socket") &&
                    evName.Contains("Connect") &&
                    (evName.EndsWith("Stop",   StringComparison.OrdinalIgnoreCase) ||
                     evName.EndsWith("End",    StringComparison.OrdinalIgnoreCase) ||
                     evName.EndsWith("Stop",   StringComparison.OrdinalIgnoreCase));
                bool isConnFail =
                    evName.Contains("Socket") &&
                    (evName.Contains("ConnectFailed", StringComparison.OrdinalIgnoreCase) ||
                     evName.Contains("Error",         StringComparison.OrdinalIgnoreCase));

                if (!isConnStart && !isConnStop && !isConnFail) continue;

                string endpoint = SafeStr(ev, "Address");
                if (endpoint.Length == 0) endpoint = SafeStr(ev, "RemoteEndPoint");
                if (endpoint.Length == 0) endpoint = SafeStr(ev, "Host");
                if (endpoint.Length == 0) endpoint = "(unknown)";

                string host = endpoint.Contains(':') ? endpoint[..endpoint.LastIndexOf(':')] : endpoint;

                if (isConnStart)
                {
                    pending[ev.ThreadID] = (ev.TimeStampRelativeMSec, endpoint);
                    int bucket = (int)(ev.TimeStampRelativeMSec / 1000.0);
                    perSecond.TryGetValue(bucket, out int pv);
                    perSecond[bucket] = pv + 1;
                }
                else if (isConnStop && pending.TryGetValue(ev.ThreadID, out var start))
                {
                    pending.Remove(ev.ThreadID);
                    double ms = ev.TimeStampRelativeMSec - start.StartMs;
                    connects++;
                    totalMs += ms;
                    if (ms > maxMs) maxMs = ms;

                    if (!byHost.TryGetValue(host, out var acc))
                        byHost[host] = acc = new HostAcc();
                    acc.Count++;
                    acc.TotalMs += ms;

                    if (ms >= SlowConnectMs)
                        slowConnects.Add(new SocketConnectEntry(start.Endpoint, ms, false,
                            start.StartMs));
                }
                else if (isConnFail)
                {
                    failures++;
                    if (!byHost.TryGetValue(host, out var acc))
                        byHost[host] = acc = new HostAcc();
                    acc.Failures++;
                    slowConnects.Add(new SocketConnectEntry(endpoint, 0, true,
                        ev.TimeStampRelativeMSec));
                }
            }
        }
        catch (Exception ex)
        {
            return new SocketTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, 0, [], [], null, false);
        }

        if (connects == 0 && failures == 0)
        {
            return new SocketTraceData(
                $"{traceFileName}  |  0 socket events — collect with " +
                "--providers 'System.Net.Sockets:0xFF:5'",
                processFilter, 0, 0, 0, 0, 0, [], [], null, false);
        }

        double avg = connects > 0 ? totalMs / connects : 0;

        var topHosts = byHost
            .OrderByDescending(kv => kv.Value.Count)
            .Take(top)
            .Select(kv => new SocketHostSummary(kv.Key, kv.Value.Count, kv.Value.Failures,
                kv.Value.TotalMs, kv.Value.Count > 0 ? kv.Value.TotalMs / kv.Value.Count : 0))
            .ToList();

        var topSlow = slowConnects.OrderByDescending(s => s.DurationMs).Take(top).ToList();

        IReadOnlyList<double>? timeline = null;
        if (perSecond.Count > 1)
        {
            int minB = perSecond.Keys.Min(), maxB = perSecond.Keys.Max();
            var tl = new double[maxB - minB + 1];
            foreach (var kv in perSecond) tl[kv.Key - minB] = kv.Value;
            timeline = tl;
        }

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {connects:N0} connects  •  {failures} failures  •  avg {avg:F1} ms";

        return new SocketTraceData(info, processFilter,
            connects, failures, avg, maxMs, topSlow.Count,
            topSlow, topHosts, timeline, HasData: true);
    }

    private static string SafeStr(TraceEvent ev, string field)
    {
        try { return ev.PayloadByName(field)?.ToString() ?? ""; } catch { return ""; }
    }

    private sealed class HostAcc { public int Count, Failures; public double TotalMs; }
}
