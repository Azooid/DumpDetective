using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

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

    public DnsTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                 string? processFilter = null, Action<string>? progress = null)
    {
        var pending     = new Dictionary<int, (double StartMs, string Host)>();
        var byHost      = new Dictionary<string, HostAcc>(StringComparer.OrdinalIgnoreCase);
        var slowList    = new List<DnsSlowResolution>();
        var failTimeline = new Dictionary<int, int>();

        int total = 0, failed = 0;
        double totalMs = 0, maxMs = 0;
        long evTotal = trace.EventCount;
        long evProcessed = 0;
        long lastProgressMs = 0;

        try
        {
            foreach (var ev in trace.Events)
            {
                evProcessed++;
                if (progress is not null && Environment.TickCount64 - lastProgressMs >= 200)
                {
                    progress($"{total:N0} DNS lookups  \u2022  {failed:N0} failed");
                    lastProgressMs = Environment.TickCount64;
                }
                if (processFilter is not null &&
                    !ev.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";
                bool isDnsStart =
                    (evName.Contains("NameResolution", StringComparison.OrdinalIgnoreCase) ||
                     evName.Contains("DnsResolut",    StringComparison.OrdinalIgnoreCase) ||
                     evName.Contains("GetHostEntry",  StringComparison.OrdinalIgnoreCase)) &&
                    (evName.EndsWith("Start", StringComparison.OrdinalIgnoreCase) ||
                     evName.EndsWith("Begin", StringComparison.OrdinalIgnoreCase));
                bool isDnsStop =
                    (evName.Contains("NameResolution", StringComparison.OrdinalIgnoreCase) ||
                     evName.Contains("DnsResolut",    StringComparison.OrdinalIgnoreCase) ||
                     evName.Contains("GetHostEntry",  StringComparison.OrdinalIgnoreCase)) &&
                    (evName.EndsWith("Stop",  StringComparison.OrdinalIgnoreCase) ||
                     evName.EndsWith("End",   StringComparison.OrdinalIgnoreCase));
                bool isDnsFail =
                    evName.Contains("NameResolution", StringComparison.OrdinalIgnoreCase) &&
                    (evName.Contains("Fail",  StringComparison.OrdinalIgnoreCase) ||
                     evName.Contains("Error", StringComparison.OrdinalIgnoreCase));

                if (!isDnsStart && !isDnsStop && !isDnsFail) continue;

                string host = SafeStr(ev, "HostName");
                if (host.Length == 0) host = SafeStr(ev, "Host");
                if (host.Length == 0) host = SafeStr(ev, "Name");
                if (host.Length == 0) host = "(unknown)";

                if (isDnsStart)
                {
                    pending[ev.ThreadID] = (ev.TimeStampRelativeMSec, host);
                }
                else if (isDnsStop && pending.TryGetValue(ev.ThreadID, out var start))
                {
                    pending.Remove(ev.ThreadID);
                    string resolvedHost = host.Length > 1 ? host : start.Host;
                    double ms = ev.TimeStampRelativeMSec - start.StartMs;
                    total++;
                    totalMs += ms;
                    if (ms > maxMs) maxMs = ms;

                    if (!byHost.TryGetValue(resolvedHost, out var acc))
                        byHost[resolvedHost] = acc = new HostAcc();
                    acc.Count++;
                    acc.TotalMs += ms;
                    if (ms > acc.MaxMs) acc.MaxMs = ms;

                    if (ms >= SlowDnsMs)
                        slowList.Add(new DnsSlowResolution(resolvedHost, ms, false,
                            start.StartMs));
                }
                else if (isDnsFail)
                {
                    failed++;
                    if (!byHost.TryGetValue(host, out var acc))
                        byHost[host] = acc = new HostAcc();
                    acc.Failures++;
                    slowList.Add(new DnsSlowResolution(host, 0, true,
                        ev.TimeStampRelativeMSec));

                    int bucket = (int)(ev.TimeStampRelativeMSec / 1000.0);
                    failTimeline.TryGetValue(bucket, out int pv);
                    failTimeline[bucket] = pv + 1;
                }
            }
        }
        catch (Exception ex)
        {
            return new DnsTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, [], [], null, false);
        }

        if (total == 0 && failed == 0)
        {
            return new DnsTraceData(
                $"{traceFileName}  |  0 DNS events — collect with " +
                "--providers 'System.Net.NameResolution:0xFF:5'",
                processFilter, 0, 0, 0, 0, [], [], null, false);
        }

        double avg = total > 0 ? totalMs / total : 0;

        var topHosts = byHost
            .OrderByDescending(kv => kv.Value.Count)
            .Take(top)
            .Select(kv => new DnsHostSummary(kv.Key, kv.Value.Count, kv.Value.Failures,
                kv.Value.TotalMs, kv.Value.Count > 0 ? kv.Value.TotalMs / kv.Value.Count : 0,
                kv.Value.MaxMs))
            .ToList();

        var topSlow = slowList.OrderByDescending(s => s.DurationMs).Take(top).ToList();

        IReadOnlyList<double>? timeline = null;
        if (failTimeline.Count > 1)
        {
            int minB = failTimeline.Keys.Min(), maxB = failTimeline.Keys.Max();
            var tl = new double[maxB - minB + 1];
            foreach (var kv in failTimeline) tl[kv.Key - minB] = kv.Value;
            timeline = tl;
        }

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {total:N0} resolutions  •  {failed} failures  •  avg {avg:F1} ms";

        return new DnsTraceData(info, processFilter,
            total, failed, avg, maxMs, topHosts, topSlow, timeline, HasData: true);
    }

    private static string SafeStr(TraceEvent ev, string field)
    {
        try { return ev.PayloadByName(field)?.ToString() ?? ""; } catch { return ""; }
    }

    private sealed class HostAcc { public int Count, Failures; public double TotalMs, MaxMs; }
}
