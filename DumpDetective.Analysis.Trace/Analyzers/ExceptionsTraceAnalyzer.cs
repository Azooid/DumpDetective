using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using static DumpDetective.Core.Tracing.TraceEventKind;


namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses first-chance exception events from a .nettrace / .etl trace.
/// Surfaces exception flood patterns by type and originating call site.
/// </summary>
public sealed class ExceptionsTraceAnalyzer
{
    public ExceptionsTraceData Analyze(string tracePath, int top = 20, string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new ExceptionsTraceData($"Failed: {ex.Message}", processFilter, 0, 0, [], []);
        }
    }

    private static readonly Dictionary<string, bool> EvKind  = new(StringComparer.OrdinalIgnoreCase);
    // Separate cache for Consume: only true for actual throw events, never for CatchStart/CatchStop.
    // Kept distinct from EvKind so WantsEvent (subscription routing) does not poison the throw-only check.
    private static readonly Dictionary<string, bool> EvThrow = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly Dictionary<string, TypeAcc> ByType = new(StringComparer.Ordinal);
        internal readonly List<ExceptionEvent> Events = new();

        public void Consume(TraceEvent ev, in TraceEventMeta meta, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            // Use EvThrow (not EvKind) so WantsEvent's routing cache cannot make CatchStart/CatchStop
            // appear as throw events.  ETL emits three events per exception (throw + CatchStart + CatchStop);
            // we only want to count the actual throw.
            if (!EvThrow.TryGetValue(meta.EventName, out bool isEx))
                EvThrow[meta.EventName] = isEx = meta.Kind switch
                {
                    _ when meta.Kind == ExceptionThrown      => true,
                    _ when meta.Kind == ExceptionCatchStart  => false,   // ETL: same exception, not a new throw
                    _ when meta.Kind == ExceptionCatchStop   => false,   // ETL: no type payload
                    _ when meta.IsKnown                      => false,
                    _ => meta.EventName.EndsWith("Exception/Start", StringComparison.OrdinalIgnoreCase) ||
                         meta.EventName.EndsWith("ExceptionThrown", StringComparison.OrdinalIgnoreCase) ||
                         (meta.EventName.EndsWith("Exception",      StringComparison.OrdinalIgnoreCase) &&
                          meta.EventName.IndexOf("Catch",           StringComparison.OrdinalIgnoreCase) < 0)
                };
            if (!isEx) return;

            string exType = SafeStr(ev, "ExceptionType");
            if (exType.Length == 0) exType = SafeStr(ev, "Type");
            if (exType.Length == 0) exType = "(unknown)";

            string msg   = SafeStr(ev, "ExceptionMessage");
            if (msg.Length == 0) msg = SafeStr(ev, "Message");
            string frame = TopUserFrame(ev);

            Events.Add(new ExceptionEvent(exType, msg, timestampMs, frame, threadId));

            if (!ByType.TryGetValue(exType, out var acc))
                ByType[exType] = acc = new TypeAcc(msg, frame);
            acc.Count++;
        }

        public bool WantsEvent(in TraceEventMeta meta)
        {
            if (!EvKind.TryGetValue(meta.EventName, out bool v))
                EvKind[meta.EventName] = v = meta.Kind switch
                {
                    _ when meta.Kind == ExceptionThrown || meta.Kind == ExceptionCatchStart || meta.Kind == ExceptionCatchStop => true,
                    _ when meta.IsKnown                                          => false,
                    _ => meta.EventName.EndsWith("Exception/Start",   StringComparison.OrdinalIgnoreCase) ||
                         meta.EventName.EndsWith("ExceptionThrown",   StringComparison.OrdinalIgnoreCase) ||
                         meta.EventName.EndsWith("Exception",         StringComparison.OrdinalIgnoreCase) ||
                         meta.EventName.IndexOf("ExceptionCatchStart", StringComparison.OrdinalIgnoreCase) >= 0
                };
            return v;
        }

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null)
        => new Consumer(processFilter);

    public ExceptionsTraceData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 20,
                                            string? processFilter = null)
    {
        var c = (Consumer)consumer;
        if (c.Events.Count == 0)
            return new ExceptionsTraceData(
                $"{traceFileName}  |  0 exception events — collect with --providers Microsoft-Windows-DotNETRuntime:0x8014:5",
                processFilter, 0, 0, [], []);

        var topTypes = c.ByType
            .OrderByDescending(kv => kv.Value.Count)
            .Take(top)
            .Select(kv => new ExceptionTypeSummary(kv.Key, kv.Value.Count,
                                                    Truncate(kv.Value.FirstMsg, 120),
                                                    kv.Value.TopFrame))
            .ToList();

        var recentEvents = c.Events
            .OrderByDescending(e => e.TimeMs)
            .Take(top)
            .ToList();

        // ── Exception rate timeline ────────────────────────────────────────────
        IReadOnlyList<double> rateTimeline = [];
        if (c.Events.Count > 1)
        {
            var perSecond = new Dictionary<int, int>();
            foreach (var ev in c.Events)
            {
                int bucket = (int)(ev.TimeMs / 1000.0);
                perSecond.TryGetValue(bucket, out int prev);
                perSecond[bucket] = prev + 1;
            }
            rateTimeline = BuildTimeline(perSecond) ?? [];
        }

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {c.Events.Count:N0} exceptions  •  {c.ByType.Count} unique types";

        return new ExceptionsTraceData(info, processFilter,
            c.Events.Count, c.ByType.Count, topTypes, recentEvents, rateTimeline);
    }

    public ExceptionsTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
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
            return new ExceptionsTraceData($"Failed: {ex.Message}", processFilter, 0, 0, [], []);
        }
    }


    private static string TopUserFrame(TraceEvent ev)
    {
        var cs = ev.CallStack();
        var cur = cs;
        while (cur is not null)
        {
            string? name = cur.CodeAddress.FullMethodName;
            // Use IsNullOrEmpty: FullMethodName returns "" (not null) for unresolved frames
            if (!string.IsNullOrEmpty(name) &&
                !name.StartsWith("System.Runtime",        StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("System.Exception",      StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("RaiseException",          StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("clr!",                    StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("ntdll",                   StringComparison.OrdinalIgnoreCase))
                return name;
            cur = cur.Caller;
        }
        // Fall back: use first frame's module name if no resolved user frame found
        var first = cs;
        while (first is not null)
        {
            string mod = first.CodeAddress.ModuleName ?? "";
            if (mod.Length > 0) return mod;
            first = first.Caller;
        }
        return "(no stack)";
    }


    private sealed class TypeAcc(string firstMsg, string topFrame)
    {
        public int    Count    = 1;
        public string FirstMsg = firstMsg;
        public string TopFrame = topFrame;
    }
}
