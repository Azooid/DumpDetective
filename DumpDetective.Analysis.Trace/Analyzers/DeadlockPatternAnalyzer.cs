using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using static DumpDetective.Core.Tracing.TraceEventKind;

namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Heuristic deadlock pattern detector.
/// Detects circular waits by finding thread pairs where:
///   - Thread A is blocked at a Contention/Start or WaitHandle/WaitStart site
///   - Thread B is also blocked at a different site
///   - Both waits overlap in time by more than <see cref="DeadlockWindowMs"/>
/// This is a best-effort heuristic — ETW does not expose lock identity.
/// </summary>
public sealed class DeadlockPatternAnalyzer
{
    private const double DeadlockWindowMs = 5_000; // 5 s sustained overlap = suspect

    public DeadlockPatternData Analyze(string tracePath, int top = 20, string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new DeadlockPatternData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, [], false);
        }
    }

    private static readonly Dictionary<string, byte> EvKind = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly Dictionary<int, ActiveWait> Active = new();
        internal readonly List<WaitChainEntry> Chains = new();
        internal int LongWaits;
        internal double TotalWait;
        internal double MaxWait;

        public void Consume(TraceEvent ev, in TraceEventMeta meta, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            if (!EvKind.TryGetValue(meta.EventName, out byte kind))
                EvKind[meta.EventName] = kind =
                    meta.EventName.EndsWith("ContentionStart",     StringComparison.OrdinalIgnoreCase) ||
                    meta.EventName.Contains("Contention/Start",    StringComparison.OrdinalIgnoreCase) ||
                    meta.EventName.EndsWith("WaitHandleWaitStart", StringComparison.OrdinalIgnoreCase) ||
                    (meta.EventName.Contains("WaitHandle", StringComparison.OrdinalIgnoreCase) &&
                     meta.EventName.Contains("Start",      StringComparison.OrdinalIgnoreCase)) ? (byte)1 :
                    meta.EventName.EndsWith("ContentionStop",      StringComparison.OrdinalIgnoreCase) ||
                    meta.EventName.Contains("Contention/Stop",     StringComparison.OrdinalIgnoreCase) ||
                    meta.EventName.EndsWith("WaitHandleWaitStop",  StringComparison.OrdinalIgnoreCase) ||
                    (meta.EventName.Contains("WaitHandle", StringComparison.OrdinalIgnoreCase) &&
                     meta.EventName.Contains("Stop",       StringComparison.OrdinalIgnoreCase)) ? (byte)2 :
                    (byte)0;
            bool isWaitStart = kind == 1;
            bool isWaitStop  = kind == 2;
            if (kind == 0) return;
            if (isWaitStart)
            {
                string frame = TopUserFrame(ev);
                Active[threadId] = new ActiveWait(timestampMs, frame, meta.EventName);

                // Check all currently active waits for overlap patterns
                foreach (var kv in Active)
                {
                    if (kv.Key == threadId) continue;
                    double overlapMs = timestampMs - kv.Value.StartMs;
                    if (overlapMs >= DeadlockWindowMs && kv.Value.Frame != frame)
                    {
                        Chains.Add(new WaitChainEntry(
                            threadId, kv.Key,
                            timestampMs, kv.Value.StartMs,
                            overlapMs, frame, kv.Value.Frame));
                    }
                }
                return;
            }

            if (isWaitStop && Active.TryGetValue(threadId, out var wait))
            {
                double waitMs = timestampMs - wait.StartMs;
                Active.Remove(threadId);
                TotalWait += waitMs;
                if (waitMs > MaxWait) MaxWait = waitMs;
                if (waitMs >= DeadlockWindowMs) LongWaits++;
            }
        }

        public bool WantsEvent(in TraceEventMeta meta)
        {
            if (!EvKind.TryGetValue(meta.EventName, out byte v))
                EvKind[meta.EventName] = v = meta.Kind switch
                {
                    _ when meta.Kind == ContentionStart || meta.Kind == WaitHandleWaitStart => 1,
                    _ when meta.Kind == ContentionStop || meta.Kind == WaitHandleWaitStop => 2,
                    _ when meta.IsKnown                    => 0,
                    _ => meta.EventName.EndsWith("ContentionStart",    StringComparison.OrdinalIgnoreCase) ||
                         meta.EventName.Contains("Contention/Start",   StringComparison.OrdinalIgnoreCase) ||
                         meta.EventName.Contains("WaitHandle",         StringComparison.OrdinalIgnoreCase) ? (byte)1
                       : meta.EventName.EndsWith("ContentionStop",     StringComparison.OrdinalIgnoreCase) ||
                         meta.EventName.Contains("Contention/Stop",    StringComparison.OrdinalIgnoreCase) ? (byte)2
                       : (byte)0
                };
            return v != 0;
        }

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null)
        => new Consumer(processFilter);

    public DeadlockPatternData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 20,
                                            string? processFilter = null)
    {
        var c = (Consumer)consumer;
        // Deduplicate chains by thread pair
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<WaitChainEntry>(c.Chains.Count);
        foreach (var ch in c.Chains.OrderByDescending(ch => ch.OverlapMs))
        {
            string key = $"{Math.Min(ch.Thread1Id, ch.Thread2Id)}-{Math.Max(ch.Thread1Id, ch.Thread2Id)}";
            if (seen.Add(key)) deduped.Add(ch);
        }

        if (deduped.Count == 0 && c.LongWaits == 0)
        {
            return new DeadlockPatternData(
                $"{traceFileName}  |  No deadlock patterns detected",
                processFilter, 0, 0, 0, 0, [], false);
        }

        var topChains = deduped.Take(top).ToList();

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {deduped.Count} suspected deadlock(s)  •  {c.LongWaits} long wait(s)";

        return new DeadlockPatternData(info, processFilter,
            deduped.Count, c.LongWaits, c.MaxWait, c.TotalWait, topChains, HasData: true);
    }

    public DeadlockPatternData Analyze(TraceLog trace, string traceFileName, int top = 20,
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
            return new DeadlockPatternData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, [], false);
        }
    }

    private static string TopUserFrame(TraceEvent ev)
    {
        var cs = ev.CallStack();
        var cur = cs;
        while (cur is not null)
        {
            string? name = cur.CodeAddress.FullMethodName;
            if (!string.IsNullOrEmpty(name) &&
                !name.Contains("Monitor",      StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("WaitHandle",   StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("clr!",         StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("ntdll",        StringComparison.OrdinalIgnoreCase))
                return name;
            cur = cur.Caller;
        }
        var first = cs;
        while (first is not null)
        {
            string mod = first.CodeAddress.ModuleName ?? "";
            if (mod.Length > 0) return mod;
            first = first.Caller;
        }
        return "(no stack)";
    }

    private sealed record ActiveWait(double StartMs, string Frame, string EventName);
}
