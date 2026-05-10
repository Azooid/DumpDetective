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

    public HandleLeakTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                        string? processFilter = null)
    {
        var byKind    = new Dictionary<string, KindAcc>(StringComparer.OrdinalIgnoreCase);
        var perSecond = new Dictionary<int, int>(); // net per second
        int netCurrent = 0;

        int created = 0, destroyed = 0;

        try
        {
            foreach (var ev in trace.Events)
            {
                if (processFilter is not null &&
                    !ev.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";
                bool isCreated =
                    evName.Contains("GCHandle", StringComparison.OrdinalIgnoreCase) &&
                    (evName.Contains("Created",  StringComparison.OrdinalIgnoreCase) ||
                     evName.Contains("Create",   StringComparison.OrdinalIgnoreCase));
                bool isDestroyed =
                    evName.Contains("GCHandle", StringComparison.OrdinalIgnoreCase) &&
                    (evName.Contains("Destroyed", StringComparison.OrdinalIgnoreCase) ||
                     evName.Contains("Destroy",   StringComparison.OrdinalIgnoreCase));

                if (!isCreated && !isDestroyed) continue;

                string kind = SafeStr(ev, "Kind");
                if (kind.Length == 0) kind = SafeStr(ev, "HandleType");
                if (kind.Length == 0) kind = "Unknown";

                if (!byKind.TryGetValue(kind, out var acc))
                    byKind[kind] = acc = new KindAcc();

                if (isCreated)
                {
                    created++;
                    acc.Created++;
                    netCurrent++;
                }
                else
                {
                    destroyed++;
                    acc.Destroyed++;
                    netCurrent = Math.Max(0, netCurrent - 1);
                }

                int bucket = (int)(ev.TimeStampRelativeMSec / 1000.0);
                perSecond[bucket] = netCurrent;
            }
        }
        catch (Exception ex)
        {
            return new HandleLeakTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, false, [], null, false);
        }

        if (created == 0 && destroyed == 0)
        {
            return new HandleLeakTraceData(
                $"{traceFileName}  |  0 GCHandle events — collect with " +
                "--providers 'Microsoft-Windows-DotNETRuntime:0x4000:5' (GCHandleKeyword)",
                processFilter, 0, 0, 0, false, [], null, false);
        }

        int net = created - destroyed;
        bool isGrowing = net > GrowthAlertThreshold;

        var breakdown = byKind
            .OrderByDescending(kv => Math.Abs(kv.Value.Created - kv.Value.Destroyed))
            .Take(top)
            .Select(kv => new HandleKindSummary(kv.Key,
                kv.Value.Created, kv.Value.Destroyed,
                kv.Value.Created - kv.Value.Destroyed))
            .ToList();

        IReadOnlyList<double>? timeline = null;
        if (perSecond.Count > 1)
        {
            int minB = perSecond.Keys.Min(), maxB = perSecond.Keys.Max();
            var tl = new double[maxB - minB + 1];
            double lastNet = 0;
            for (int b = 0; b <= maxB - minB; b++)
            {
                lastNet = perSecond.TryGetValue(b + minB, out int v) ? v : lastNet;
                tl[b] = lastNet;
            }
            timeline = tl;
        }

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {created:N0} created  •  {destroyed:N0} destroyed  •  net +{net}" +
                      (isGrowing ? "  ⚠ leak suspected" : "");

        return new HandleLeakTraceData(info, processFilter,
            created, destroyed, net, isGrowing, breakdown, timeline, HasData: true);
    }

    private static string SafeStr(TraceEvent ev, string field)
    {
        try { return ev.PayloadByName(field)?.ToString() ?? ""; } catch { return ""; }
    }

    private sealed class KindAcc { public int Created, Destroyed; }
}
