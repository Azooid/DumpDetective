using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

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

    public DeadlockPatternData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                        string? processFilter = null, Action<string>? progress = null)
    {
        // Active waits: ThreadID → (StartMs, Frame, WaitType)
        var active = new Dictionary<int, ActiveWait>();
        var chains = new List<WaitChainEntry>();
        int longWaits = 0;
        double totalWait = 0;
        double maxWait = 0;
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
                    progress($"{longWaits:N0} long waits  \u2022  {chains.Count:N0} suspect chains");
                    lastProgressMs = Environment.TickCount64;
                }
                if (processFilter is not null &&
                    !ev.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";
                bool isWaitStart =
                    evName.EndsWith("ContentionStart",    StringComparison.OrdinalIgnoreCase) ||
                    evName.Contains("Contention/Start",   StringComparison.OrdinalIgnoreCase) ||
                    evName.EndsWith("WaitHandleWaitStart",StringComparison.OrdinalIgnoreCase) ||
                    (evName.Contains("WaitHandle", StringComparison.OrdinalIgnoreCase) &&
                     evName.Contains("Start",     StringComparison.OrdinalIgnoreCase));
                bool isWaitStop =
                    evName.EndsWith("ContentionStop",    StringComparison.OrdinalIgnoreCase) ||
                    evName.Contains("Contention/Stop",   StringComparison.OrdinalIgnoreCase) ||
                    evName.EndsWith("WaitHandleWaitStop",StringComparison.OrdinalIgnoreCase) ||
                    (evName.Contains("WaitHandle", StringComparison.OrdinalIgnoreCase) &&
                     evName.Contains("Stop",      StringComparison.OrdinalIgnoreCase));

                if (isWaitStart)
                {
                    string frame = TopUserFrame(ev);
                    active[ev.ThreadID] = new ActiveWait(ev.TimeStampRelativeMSec, frame, evName);

                    // Check all currently active waits for overlap patterns
                    // A deadlock candidate = two threads blocked at different sites simultaneously
                    foreach (var kv in active)
                    {
                        if (kv.Key == ev.ThreadID) continue;
                        double overlapMs = ev.TimeStampRelativeMSec - kv.Value.StartMs;
                        if (overlapMs >= DeadlockWindowMs && kv.Value.Frame != frame)
                        {
                            chains.Add(new WaitChainEntry(
                                ev.ThreadID, kv.Key,
                                ev.TimeStampRelativeMSec, kv.Value.StartMs,
                                overlapMs, frame, kv.Value.Frame));
                        }
                    }
                    continue;
                }

                if (isWaitStop && active.TryGetValue(ev.ThreadID, out var wait))
                {
                    double waitMs = ev.TimeStampRelativeMSec - wait.StartMs;
                    active.Remove(ev.ThreadID);
                    totalWait += waitMs;
                    if (waitMs > maxWait) maxWait = waitMs;
                    if (waitMs >= DeadlockWindowMs) longWaits++;
                }
            }
        }
        catch (Exception ex)
        {
            return new DeadlockPatternData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, [], false);
        }

        // Deduplicate chains by thread pair
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<WaitChainEntry>(chains.Count);
        foreach (var c in chains.OrderByDescending(c => c.OverlapMs))
        {
            string key = $"{Math.Min(c.Thread1Id, c.Thread2Id)}-{Math.Max(c.Thread1Id, c.Thread2Id)}";
            if (seen.Add(key)) deduped.Add(c);
        }

        if (deduped.Count == 0 && longWaits == 0)
        {
            return new DeadlockPatternData(
                $"{traceFileName}  |  No deadlock patterns detected",
                processFilter, 0, 0, 0, 0, [], false);
        }

        var topChains = deduped.Take(top).ToList();

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {deduped.Count} suspected deadlock(s)  •  {longWaits} long wait(s)";

        return new DeadlockPatternData(info, processFilter,
            deduped.Count, longWaits, maxWait, totalWait, topChains, HasData: true);
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
