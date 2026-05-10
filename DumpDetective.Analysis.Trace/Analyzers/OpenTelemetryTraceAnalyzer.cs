using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses System.Diagnostics.DiagnosticSource/ActivityStart/ActivityStop events
/// (OpenTelemetry activities) to detect slow operations and error-rate spikes.
/// Provider: System.Diagnostics.DiagnosticSource — available in .nettrace and ETL.
/// </summary>
public sealed class OpenTelemetryTraceAnalyzer
{
    private const double SlowActivityMs = 1000.0;

    public OpenTelemetryTraceData Analyze(string tracePath, int top = 20, string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new OpenTelemetryTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, [], [], null, false);
        }
    }

    public OpenTelemetryTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                           string? processFilter = null)
    {
        var pending     = new Dictionary<string, (double StartMs, string Op, bool IsError)>(StringComparer.Ordinal);
        var byOp        = new Dictionary<string, OpAcc>(StringComparer.OrdinalIgnoreCase);
        var slowList    = new List<OtelSlowActivity>();
        var errTimeline = new Dictionary<int, int>();

        int total = 0, totalErrors = 0;

        try
        {
            foreach (var ev in trace.Events)
            {
                if (processFilter is not null &&
                    !ev.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";
                bool isDiagSrc = ev.ProviderName.Contains("DiagnosticSource",    StringComparison.OrdinalIgnoreCase) ||
                                 ev.ProviderName.Contains("System.Diagnostics",  StringComparison.OrdinalIgnoreCase) ||
                                 evName.Contains("Activity",                     StringComparison.OrdinalIgnoreCase);
                if (!isDiagSrc) continue;

                bool isStart = evName.EndsWith("Start", StringComparison.OrdinalIgnoreCase) ||
                               evName.EndsWith("Begin", StringComparison.OrdinalIgnoreCase);
                bool isStop  = evName.EndsWith("Stop",  StringComparison.OrdinalIgnoreCase) ||
                               evName.EndsWith("End",   StringComparison.OrdinalIgnoreCase);

                if (!isStart && !isStop) continue;

                string opName = SafeStr(ev, "OperationName");
                if (opName.Length == 0) opName = SafeStr(ev, "Name");
                if (opName.Length == 0) opName = evName;

                string actId  = SafeStr(ev, "ActivityId");
                if (actId.Length == 0) actId  = $"{ev.ThreadID}_{opName}";

                bool hasError = SafeStr(ev, "Error").Length > 0 ||
                                SafeStr(ev, "Status").Equals("Error", StringComparison.OrdinalIgnoreCase);

                if (isStart)
                {
                    pending[actId] = (ev.TimeStampRelativeMSec, opName, hasError);
                }
                else if (isStop && pending.TryGetValue(actId, out var start))
                {
                    pending.Remove(actId);
                    double ms = ev.TimeStampRelativeMSec - start.StartMs;
                    bool isError = start.IsError || hasError ||
                                   SafeStr(ev, "Status").Equals("Error", StringComparison.OrdinalIgnoreCase);

                    total++;
                    if (isError) totalErrors++;

                    if (!byOp.TryGetValue(start.Op, out var acc))
                        byOp[start.Op] = acc = new OpAcc();
                    acc.Count++;
                    acc.TotalMs += ms;
                    if (ms > acc.MaxMs) acc.MaxMs = ms;
                    if (isError) acc.Errors++;

                    if (isError)
                    {
                        int bucket = (int)(ev.TimeStampRelativeMSec / 1000.0);
                        errTimeline.TryGetValue(bucket, out int pv);
                        errTimeline[bucket] = pv + 1;
                    }

                    if (ms >= SlowActivityMs)
                        slowList.Add(new OtelSlowActivity(start.Op, ms, isError, start.StartMs));
                }
            }
        }
        catch (Exception ex)
        {
            return new OpenTelemetryTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, [], [], null, false);
        }

        if (total == 0)
        {
            return new OpenTelemetryTraceData(
                $"{traceFileName}  |  0 Activity events — collect with " +
                "--providers 'System.Diagnostics.DiagnosticSource:0xFF:5' or enable OpenTelemetry SDK",
                processFilter, 0, 0, [], [], null, false);
        }

        var topOps = byOp
            .OrderByDescending(kv => kv.Value.Count)
            .Take(top)
            .Select(kv => new OtelOperationSummary(kv.Key,
                kv.Value.Count, kv.Value.Errors,
                kv.Value.TotalMs, kv.Value.Count > 0 ? kv.Value.TotalMs / kv.Value.Count : 0,
                kv.Value.MaxMs))
            .ToList();

        var topSlow = slowList.OrderByDescending(s => s.DurationMs).Take(top).ToList();

        IReadOnlyList<double>? timeline = null;
        if (errTimeline.Count > 1)
        {
            int minB = errTimeline.Keys.Min(), maxB = errTimeline.Keys.Max();
            var tl = new double[maxB - minB + 1];
            foreach (var kv in errTimeline) tl[kv.Key - minB] = kv.Value;
            timeline = tl;
        }

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {total:N0} activities  •  {totalErrors} errors  •  {topSlow.Count} slow";

        return new OpenTelemetryTraceData(info, processFilter,
            total, totalErrors, topOps, topSlow, timeline, HasData: true);
    }

    private static string SafeStr(TraceEvent ev, string field)
    {
        try { return ev.PayloadByName(field)?.ToString() ?? ""; } catch { return ""; }
    }

    private sealed class OpAcc { public int Count, Errors; public double TotalMs, MaxMs; }
}
