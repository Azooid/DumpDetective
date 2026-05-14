using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;


namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses GCHandle/Created and GCHandle/Destroyed events (keyword 0x4000) to detect
/// handle leaks — net-positive growth in created vs. destroyed handles over the trace.
/// </summary>
public sealed class HandleLeakTraceAnalyzer
{
    private const int GrowthAlertThreshold = 100; // net gain > 100 = likely leak

    public HandleLeakTraceData Analyze(string tracePath, int top = 20, string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new HandleLeakTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, false, [], null, false);
        }
    }

    private static readonly Dictionary<string, byte> EvKind = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly Dictionary<string, KindAcc> ByKind = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<int, int> PerSecond = new();
        internal int NetCurrent;
        internal int Created, Destroyed;

        public void Consume(TraceEvent ev, string evName, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            if (!EvKind.TryGetValue(evName, out byte kind))
                EvKind[evName] = kind =
                    evName.Contains("GCHandle", StringComparison.OrdinalIgnoreCase) &&
                    (evName.Contains("Created",  StringComparison.OrdinalIgnoreCase) ||
                     evName.Contains("Create",   StringComparison.OrdinalIgnoreCase)) ? (byte)1 :
                    evName.Contains("GCHandle", StringComparison.OrdinalIgnoreCase) &&
                    (evName.Contains("Destroyed", StringComparison.OrdinalIgnoreCase) ||
                     evName.Contains("Destroy",   StringComparison.OrdinalIgnoreCase)) ? (byte)2 :
                    (byte)0;
            if (kind == 0) return;
            bool isCreated   = kind == 1;
            bool isDestroyed = kind == 2;

            string handleKind = SafeStr(ev, "Kind");
            if (handleKind.Length == 0) handleKind = SafeStr(ev, "HandleType");
            if (handleKind.Length == 0) handleKind = "Unknown";

            if (!ByKind.TryGetValue(handleKind, out var acc))
                ByKind[handleKind] = acc = new KindAcc();

            if (isCreated)
            {
                Created++;
                acc.Created++;
                NetCurrent++;
            }
            else
            {
                Destroyed++;
                acc.Destroyed++;
                NetCurrent = Math.Max(0, NetCurrent - 1);
            }

            int bucket = (int)(timestampMs / 1000.0);
            PerSecond[bucket] = NetCurrent;
        }

        public bool WantsEvent(string eventName) { if (!EvKind.TryGetValue(eventName, out byte v)) { v = eventName.Contains("GCHandle", StringComparison.OrdinalIgnoreCase) && (eventName.Contains("Created", StringComparison.OrdinalIgnoreCase) || eventName.Contains("Create", StringComparison.OrdinalIgnoreCase)) ? (byte)1 : eventName.Contains("GCHandle", StringComparison.OrdinalIgnoreCase) && (eventName.Contains("Destroyed", StringComparison.OrdinalIgnoreCase) || eventName.Contains("Destroy", StringComparison.OrdinalIgnoreCase)) ? (byte)2 : (byte)0; EvKind[eventName] = v; } return v != 0; }

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null)
        => new Consumer(processFilter);

    public HandleLeakTraceData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 20,
                                            string? processFilter = null)
    {
        var c = (Consumer)consumer;
        if (c.Created == 0 && c.Destroyed == 0)
        {
            return new HandleLeakTraceData(
                $"{traceFileName}  |  0 GCHandle events — collect with " +
                "--providers 'Microsoft-Windows-DotNETRuntime:0x4000:5' (GCHandleKeyword)",
                processFilter, 0, 0, 0, false, [], null, false);
        }

        int net = c.Created - c.Destroyed;
        bool isGrowing = net > GrowthAlertThreshold;

        var breakdown = c.ByKind
            .OrderByDescending(kv => Math.Abs(kv.Value.Created - kv.Value.Destroyed))
            .Take(top)
            .Select(kv => new HandleKindSummary(kv.Key,
                kv.Value.Created, kv.Value.Destroyed,
                kv.Value.Created - kv.Value.Destroyed))
            .ToList();

        IReadOnlyList<double>? timeline = null;
        if (c.PerSecond.Count > 1)
        {
            int minB = c.PerSecond.Keys.Min(), maxB = c.PerSecond.Keys.Max();
            var tl = new double[maxB - minB + 1];
            double lastNet = 0;
            for (int b = 0; b <= maxB - minB; b++)
            {
                lastNet = c.PerSecond.TryGetValue(b + minB, out int v) ? v : lastNet;
                tl[b] = lastNet;
            }
            timeline = tl;
        }

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {c.Created:N0} created  •  {c.Destroyed:N0} destroyed  •  net +{net}" +
                      (isGrowing ? "  ⚠ leak suspected" : "");

        return new HandleLeakTraceData(info, processFilter,
            c.Created, c.Destroyed, net, isGrowing, breakdown, timeline, HasData: true);
    }

    public HandleLeakTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
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
            return new HandleLeakTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, false, [], null, false);
        }
    }


    private sealed class KindAcc { public int Created, Destroyed; }
}
