using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using static DumpDetective.Core.Tracing.TraceEventKind;

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

        public void Consume(TraceEvent ev, in TraceEventMeta meta, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            bool isKestrel = meta.EventName.Contains("Kestrel", StringComparison.OrdinalIgnoreCase) ||
                             ev.ProviderName.Contains("Kestrel", StringComparison.OrdinalIgnoreCase);
            if (!isKestrel) return;

            if (meta.EventName.Contains("ConnectionStart",    StringComparison.OrdinalIgnoreCase) ||
                (meta.EventName.Contains("Connection",        StringComparison.OrdinalIgnoreCase) &&
                 meta.EventName.EndsWith("Start",             StringComparison.OrdinalIgnoreCase)))
            {
                Connections++;
                CurrentConcurrent++;
                if (CurrentConcurrent > Peak) Peak = CurrentConcurrent;

                int bucket = (int)(timestampMs / 1000.0);
                PerSecond.TryGetValue(bucket, out int pv);
                PerSecond[bucket] = Math.Max(pv, CurrentConcurrent);
            }
            else if (meta.EventName.Contains("ConnectionStop",  StringComparison.OrdinalIgnoreCase) ||
                     (meta.EventName.Contains("Connection",     StringComparison.OrdinalIgnoreCase) &&
                      meta.EventName.EndsWith("Stop",           StringComparison.OrdinalIgnoreCase)))
            {
                if (CurrentConcurrent > 0) CurrentConcurrent--;
            }
            else if (meta.EventName.Contains("Reject",           StringComparison.OrdinalIgnoreCase) ||
                     meta.EventName.Contains("ConnectionRejected",StringComparison.OrdinalIgnoreCase))
            {
                Rejected++;
            }
            else if (meta.EventName.Contains("RequestError",   StringComparison.OrdinalIgnoreCase) ||
                     (meta.EventName.Contains("Request",       StringComparison.OrdinalIgnoreCase) &&
                      meta.EventName.Contains("Error",         StringComparison.OrdinalIgnoreCase)))
            {
                Errors++;
            }
            else if (meta.EventName.Contains("Queue",          StringComparison.OrdinalIgnoreCase))
            {
                QueuePressure = true;
            }
        }

        public bool WantsEvent(in TraceEventMeta meta) => meta.Kind switch
        {
            _ when meta.Kind == KestrelConnectionStart || meta.Kind == KestrelConnectionStop ||
                 meta.Kind == KestrelConnectionRejected || meta.Kind == KestrelRequestError ||
                 meta.Kind == KestrelConnectionQueueStart || meta.Kind == KestrelConnectionQueueStop => true,
            _ when meta.ProviderName.Contains("Kestrel", StringComparison.OrdinalIgnoreCase) => true,
            _ when meta.IsKnown => false,
            _ => meta.EventName.Contains("Kestrel",    StringComparison.OrdinalIgnoreCase) ||
                 meta.EventName.Contains("Connection", StringComparison.OrdinalIgnoreCase) ||
                 meta.EventName.Contains("Request",    StringComparison.OrdinalIgnoreCase)
        };

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
