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

    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal int Connections, Rejected, Errors;
        internal int CurrentConcurrent, Peak;
        internal bool QueuePressure;
        internal readonly Dictionary<int, int> PerSecond = new();

        public void Consume(TraceEvent ev, string evName, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            bool isKestrel = evName.Contains("Kestrel", StringComparison.OrdinalIgnoreCase) ||
                             ev.ProviderName.Contains("Kestrel", StringComparison.OrdinalIgnoreCase);
            if (!isKestrel) return;

            if (evName.Contains("ConnectionStart",    StringComparison.OrdinalIgnoreCase) ||
                (evName.Contains("Connection",        StringComparison.OrdinalIgnoreCase) &&
                 evName.EndsWith("Start",             StringComparison.OrdinalIgnoreCase)))
            {
                Connections++;
                CurrentConcurrent++;
                if (CurrentConcurrent > Peak) Peak = CurrentConcurrent;

                int bucket = (int)(timestampMs / 1000.0);
                PerSecond.TryGetValue(bucket, out int pv);
                PerSecond[bucket] = Math.Max(pv, CurrentConcurrent);
            }
            else if (evName.Contains("ConnectionStop",  StringComparison.OrdinalIgnoreCase) ||
                     (evName.Contains("Connection",     StringComparison.OrdinalIgnoreCase) &&
                      evName.EndsWith("Stop",           StringComparison.OrdinalIgnoreCase)))
            {
                if (CurrentConcurrent > 0) CurrentConcurrent--;
            }
            else if (evName.Contains("Reject",           StringComparison.OrdinalIgnoreCase) ||
                     evName.Contains("ConnectionRejected",StringComparison.OrdinalIgnoreCase))
            {
                Rejected++;
            }
            else if (evName.Contains("RequestError",   StringComparison.OrdinalIgnoreCase) ||
                     (evName.Contains("Request",       StringComparison.OrdinalIgnoreCase) &&
                      evName.Contains("Error",         StringComparison.OrdinalIgnoreCase)))
            {
                Errors++;
            }
            else if (evName.Contains("Queue",          StringComparison.OrdinalIgnoreCase))
            {
                QueuePressure = true;
            }
        }

        public bool WantsEvent(string eventName) => eventName.Contains("Kestrel", StringComparison.OrdinalIgnoreCase) || eventName.Contains("Connection", StringComparison.OrdinalIgnoreCase) || eventName.Contains("Request", StringComparison.OrdinalIgnoreCase);

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null)
        => new Consumer(processFilter);

    public KestrelTraceData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 20,
                                         string? processFilter = null)
    {
        var c = (Consumer)consumer;
        if (c.Connections == 0 && c.Rejected == 0 && c.Errors == 0)
        {
            return new KestrelTraceData(
                $"{traceFileName}  |  0 Kestrel events — collect with " +
                "--providers 'Microsoft-AspNetCore-Server-Kestrel:0xFF:5'",
                processFilter, 0, 0, 0, 0, false, null, false);
        }

        var timeline = BuildTimeline(c.PerSecond);

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {c.Connections:N0} connections  •  {c.Rejected} rejected  •  peak {c.Peak}";

        return new KestrelTraceData(info, processFilter,
            c.Connections, c.Rejected, c.Peak, c.Errors, c.QueuePressure, timeline, HasData: true);
    }

    public KestrelTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
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
            return new KestrelTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, false, null, false);
        }
    }
}
