using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using static DumpDetective.Core.Tracing.TraceEventKind;


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

    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly Dictionary<string, (double StartMs, string Op, bool IsError)> Pending = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, OpAcc> ByOp = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<OtelSlowActivity> SlowList = new();
        internal readonly Dictionary<int, int> ErrTimeline = new();
        internal int Total, TotalErrors;

        public void Consume(TraceEvent ev, in TraceEventMeta meta, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            bool isDiagSrc = ev.ProviderName.Contains("DiagnosticSource",    StringComparison.OrdinalIgnoreCase) ||
                             ev.ProviderName.Contains("System.Diagnostics",  StringComparison.OrdinalIgnoreCase) ||
                             meta.EventName.Contains("Activity",             StringComparison.OrdinalIgnoreCase);
            if (!isDiagSrc) return;

            // Microsoft-Diagnostics-DiagnosticSource wraps real events in SourceName+EventName payload.
            // The outer EventName is always "Event" which doesn't end in Start/Stop — unwrap it.
            string eventNameToCheck = meta.EventName;
            if (meta.EventName.Equals("Event", StringComparison.OrdinalIgnoreCase) ||
                meta.EventName.EndsWith("/Event", StringComparison.OrdinalIgnoreCase))
            {
                string inner = SafeStr(ev, "EventName");
                if (inner.Length > 0) eventNameToCheck = inner;
            }

            bool isStart = eventNameToCheck.EndsWith("Start", StringComparison.OrdinalIgnoreCase) ||
                           eventNameToCheck.EndsWith("Begin", StringComparison.OrdinalIgnoreCase);
            bool isStop  = eventNameToCheck.EndsWith("Stop",  StringComparison.OrdinalIgnoreCase) ||
                           eventNameToCheck.EndsWith("End",   StringComparison.OrdinalIgnoreCase);

            if (!isStart && !isStop) return;

            string opName = SafeStr(ev, "OperationName");
            if (opName.Length == 0) opName = SafeStr(ev, "Name");
            if (opName.Length == 0)
            {
                // Derive a readable short name from inner event name, stripping the Start/Stop suffix.
                // e.g. "Microsoft.AspNetCore.Hosting.HttpRequestIn.Start" → "HttpRequestIn"
                int suffixLen = eventNameToCheck.EndsWith("Start", StringComparison.OrdinalIgnoreCase) ||
                                eventNameToCheck.EndsWith("Begin", StringComparison.OrdinalIgnoreCase) ? 6
                              : eventNameToCheck.EndsWith("Stop", StringComparison.OrdinalIgnoreCase) ? 5
                              : 4; // ".End"
                string baseName = eventNameToCheck.Length > suffixLen
                    ? eventNameToCheck[..^suffixLen].TrimEnd('.')
                    : eventNameToCheck;
                int lastDot = baseName.LastIndexOf('.');
                opName = lastDot >= 0 ? baseName[(lastDot + 1)..] : baseName;
                if (opName.Length == 0) opName = eventNameToCheck;
            }

            string actId = SafeStr(ev, "ActivityId");
            if (actId.Length == 0 && ev.ActivityID != Guid.Empty)
                actId = ev.ActivityID.ToString();
            if (actId.Length == 0) actId = $"{threadId}_{opName}";

            bool hasError = SafeStr(ev, "Error").Length > 0 ||
                            SafeStr(ev, "Status").Equals("Error", StringComparison.OrdinalIgnoreCase);

            if (isStart)
            {
                Pending[actId] = (timestampMs, opName, hasError);
            }
            else if (isStop && Pending.TryGetValue(actId, out var start))
            {
                Pending.Remove(actId);
                double ms = timestampMs - start.StartMs;
                bool isError = start.IsError || hasError ||
                               SafeStr(ev, "Status").Equals("Error", StringComparison.OrdinalIgnoreCase);

                Total++;
                if (isError) TotalErrors++;

                if (!ByOp.TryGetValue(start.Op, out var acc))
                    ByOp[start.Op] = acc = new OpAcc();
                acc.Count++;
                acc.TotalMs += ms;
                if (ms > acc.MaxMs) acc.MaxMs = ms;
                if (isError) acc.Errors++;

                if (isError)
                {
                    int bucket = (int)(timestampMs / 1000.0);
                    ErrTimeline.TryGetValue(bucket, out int pv);
                    ErrTimeline[bucket] = pv + 1;
                }

                if (ms >= SlowActivityMs)
                    SlowList.Add(new OtelSlowActivity(start.Op, ms, isError, start.StartMs));
            }
        }

        public bool WantsEvent(in TraceEventMeta meta) => meta.Kind switch
        {
            _ when meta.Kind == ActivityStart || meta.Kind == ActivityStop => true,
            _ when meta.ProviderName.Contains("DiagnosticSource",  StringComparison.OrdinalIgnoreCase) ||
                   meta.ProviderName.Contains("System.Diagnostics",StringComparison.OrdinalIgnoreCase) => true,
            _ when meta.IsKnown => false,
            _ => meta.EventName.Contains("Activity", StringComparison.OrdinalIgnoreCase)
        };

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null)
        => new Consumer(processFilter);

    public OpenTelemetryTraceData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 20,
                                               string? processFilter = null)
    {
        var c = (Consumer)consumer;
        if (c.Total == 0)
        {
            return new OpenTelemetryTraceData(
                $"{traceFileName}  |  0 Activity events — collect with " +
                "--providers 'System.Diagnostics.DiagnosticSource:0xFF:5' or enable OpenTelemetry SDK",
                processFilter, 0, 0, [], [], null, false);
        }

        var topOps = c.ByOp
            .OrderByDescending(kv => kv.Value.Count)
            .Take(top)
            .Select(kv => new OtelOperationSummary(kv.Key,
                kv.Value.Count, kv.Value.Errors,
                kv.Value.TotalMs, kv.Value.Count > 0 ? kv.Value.TotalMs / kv.Value.Count : 0,
                kv.Value.MaxMs))
            .ToList();

        var topSlow = c.SlowList.OrderByDescending(s => s.DurationMs).Take(top).ToList();

        var timeline = BuildTimeline(c.ErrTimeline);

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {c.Total:N0} activities  •  {c.TotalErrors} errors  •  {topSlow.Count} slow";

        return new OpenTelemetryTraceData(info, processFilter,
            c.Total, c.TotalErrors, topOps, topSlow, timeline, HasData: true);
    }

    public OpenTelemetryTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
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
            return new OpenTelemetryTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, [], [], null, false);
        }
    }


    private sealed class OpAcc { public int Count, Errors; public double TotalMs, MaxMs; }
}
