using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses Microsoft-AspNetCore-Server-Kestrel EventSource events to detect
/// connection queue pressure, rejected connections, and request errors.
/// </summary>
public sealed class KestrelTraceAnalyzer
{
    public KestrelTraceData Analyze(string tracePath, int top = 20, string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new KestrelTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, false, null, false);
        }
    }

    public KestrelTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                     string? processFilter = null, Action<string>? progress = null)
    {
        int connections = 0, rejected = 0, errors = 0;
        int currentConcurrent = 0, peak = 0;
        bool queuePressure = false;
        var perSecond = new Dictionary<int, int>();
        long total = trace.EventCount;
        long processed = 0;
        long lastProgressMs = 0;

        try
        {
            foreach (var ev in trace.Events)
            {
                processed++;
                if (progress is not null && Environment.TickCount64 - lastProgressMs >= 200)
                {
                    progress($"{connections:N0} connections  \u2022  {rejected:N0} rejected");
                    lastProgressMs = Environment.TickCount64;
                }
                if (processFilter is not null &&
                    !ev.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";
                bool isKestrel = evName.Contains("Kestrel", StringComparison.OrdinalIgnoreCase) ||
                                 ev.ProviderName.Contains("Kestrel", StringComparison.OrdinalIgnoreCase);
                if (!isKestrel) continue;

                if (evName.Contains("ConnectionStart",    StringComparison.OrdinalIgnoreCase) ||
                    (evName.Contains("Connection",        StringComparison.OrdinalIgnoreCase) &&
                     evName.EndsWith("Start",             StringComparison.OrdinalIgnoreCase)))
                {
                    connections++;
                    currentConcurrent++;
                    if (currentConcurrent > peak) peak = currentConcurrent;

                    int bucket = (int)(ev.TimeStampRelativeMSec / 1000.0);
                    perSecond.TryGetValue(bucket, out int pv);
                    perSecond[bucket] = Math.Max(pv, currentConcurrent);
                }
                else if (evName.Contains("ConnectionStop",  StringComparison.OrdinalIgnoreCase) ||
                         (evName.Contains("Connection",     StringComparison.OrdinalIgnoreCase) &&
                          evName.EndsWith("Stop",           StringComparison.OrdinalIgnoreCase)))
                {
                    if (currentConcurrent > 0) currentConcurrent--;
                }
                else if (evName.Contains("Reject",           StringComparison.OrdinalIgnoreCase) ||
                         evName.Contains("ConnectionRejected",StringComparison.OrdinalIgnoreCase))
                {
                    rejected++;
                }
                else if (evName.Contains("RequestError",   StringComparison.OrdinalIgnoreCase) ||
                         (evName.Contains("Request",       StringComparison.OrdinalIgnoreCase) &&
                          evName.Contains("Error",         StringComparison.OrdinalIgnoreCase)))
                {
                    errors++;
                }
                else if (evName.Contains("Queue",          StringComparison.OrdinalIgnoreCase))
                {
                    queuePressure = true;
                }
            }
        }
        catch (Exception ex)
        {
            return new KestrelTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, false, null, false);
        }

        if (connections == 0 && rejected == 0 && errors == 0)
        {
            return new KestrelTraceData(
                $"{traceFileName}  |  0 Kestrel events — collect with " +
                "--providers 'Microsoft-AspNetCore-Server-Kestrel:0xFF:5'",
                processFilter, 0, 0, 0, 0, false, null, false);
        }

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
                      $"  |  {connections:N0} connections  •  {rejected} rejected  •  peak {peak}";

        return new KestrelTraceData(info, processFilter,
            connections, rejected, peak, errors, queuePressure, timeline, HasData: true);
    }
}
