using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Analysis.Trace;

/// <summary>
/// Summary produced by <see cref="TraceEventDispatcher.Dispatch"/> after the
/// single-pass walk completes.
/// </summary>
/// <param name="TotalEvents">Total events iterated.</param>
/// <param name="EventCounts">
///   Per-event-name occurrence count.
///   Keys are the raw <c>TraceEvent.EventName</c> strings (e.g. <c>"GC/Start"</c>).
/// </param>
/// <param name="ConsumerCounts">
///   Number of registered consumers that opted in to each event name via
///   <see cref="ITraceEventConsumer.WantsEvent"/>.
/// </param>
/// <param name="ProviderNames">
///   Maps each unique <c>TraceEvent.EventName</c> to the raw
///   <c>TraceEvent.ProviderName</c> observed on its first occurrence.
/// </param>
public readonly record struct DispatchStats(
    long TotalEvents,
    IReadOnlyDictionary<string, long>   EventCounts,
    IReadOnlyDictionary<string, int>    ConsumerCounts,
    IReadOnlyDictionary<string, string> ProviderNames);

/// <summary>
/// Iterates <c>trace.Events</c> ONCE and fans out to every registered
/// <see cref="ITraceEventConsumer"/> — replacing the previous pattern where
/// each analyzer maintained its own independent <c>foreach</c> loop over
/// the same file.
///
/// Usage (orchestrator):
/// <code>
///   var consumers = subAnalyzers
///       .Where(s => s.SupportsConsumer)
///       .Select(s => s.CreateConsumer(params, fileName))
///       .ToList();
///
///   TraceEventDispatcher.Dispatch(trace, consumers);
///
///   foreach (var sub in subAnalyzers.Where(s => s.SupportsConsumer))
///       sub.CompleteFromConsumer(consumerMap[sub], ...);
/// </code>
///
/// Usage (standalone analyzer — single consumer convenience):
/// <code>
///   var c = analyzer.CreateConsumer(processFilter);
///   TraceEventDispatcher.Dispatch(trace, c);
///   return analyzer.BuildResult(c, traceFileName, top);
/// </code>
/// </summary>
public static class TraceEventDispatcher
{
    /// <summary>
    /// Fans every event in <paramref name="trace"/> out to all
    /// <paramref name="consumers"/> in registration order.
    /// <see cref="ITraceEventConsumer.OnComplete"/> is guaranteed to be called
    /// on every consumer even if the pass throws.
    /// </summary>
    /// <param name="progress">
    /// Optional callback invoked every ~200 ms with a formatted status string
    /// showing total events processed and current throughput (events/sec).
    /// </param>

    public static DispatchStats Dispatch(
        TraceLog                           trace,
        IReadOnlyList<ITraceEventConsumer> consumers,
        Action<string>?                    progress = null)
    {
        long count     = 0;
        long lastCount = 0;

        var sw        = progress is not null ? Stopwatch.StartNew() : null;
        long lastMs   = 0;

        // Routing table: evName → subset of consumers that want this event type.
        // Built lazily on first occurrence of each unique event name so the per-event
        // inner loop dispatches only to interested consumers instead of all 20+.
        var routing       = new Dictionary<string, ITraceEventConsumer[]>(128, StringComparer.OrdinalIgnoreCase);
        var eventCounts   = new Dictionary<string, long>(128, StringComparer.OrdinalIgnoreCase);
        var providerNames = new Dictionary<string, string>(128, StringComparer.OrdinalIgnoreCase);

        try
        {
            // Sequential event iteration + sequential consumer fan-out.
            //
            // Both parallelism strategies were attempted and rejected:
            //   Strategy A — Parallel.ForEach over events: consumers use plain Dictionary,
            //                not thread-safe for concurrent Consume() calls.
            //   Strategy B — Parallel.For over consumers per event: TraceEvent itself is
            //                not thread-safe (CallStack(), ProcessName, etc. share internal
            //                mutable state) — concurrent reads from multiple consumers on the
            //                same event throw NullReferenceException.
            foreach (var ev in trace.Events)
            {
                string evName   = ev.EventName ?? "";
                string procName = ev.ProcessName ?? "";
                double tsMs     = ev.TimeStampRelativeMSec;
                int    threadId = ev.ThreadID;

                if (!routing.TryGetValue(evName, out var targets))
                {
                    // First time we see this event name — ask each consumer once, cache result.
                    var buf = new List<ITraceEventConsumer>(consumers.Count);
                    for (int i = 0; i < consumers.Count; i++)
                        if (consumers[i].WantsEvent(evName))
                            buf.Add(consumers[i]);
                    routing[evName]       = targets = buf.ToArray();
                    eventCounts[evName]   = 0;
                    providerNames[evName] = ev.ProviderName ?? "";
                }

                System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(eventCounts, evName, out _)++;

                for (int i = 0; i < targets.Length; i++)
                    targets[i].Consume(ev, evName, procName, tsMs, threadId);

                count++;
                if (sw is not null)
                {
                    long nowMs = sw.ElapsedMilliseconds;
                    if (nowMs - lastMs >= 200)
                    {
                        long elapsed = nowMs - lastMs;
                        long delta   = count - lastCount;
                        long evPerSec = elapsed > 0 ? delta * 1000 / elapsed : 0;
                        progress!($"{count:N0} events  •  {evPerSec:N0} ev/s");
                        lastMs    = nowMs;
                        lastCount = count;
                    }
                }
            }
        }
        finally
        {
            for (int i = 0; i < consumers.Count; i++)
                consumers[i].OnComplete();
        }

        // Build ConsumerCounts from routing table (already complete after the walk).
        var consumerCounts = new Dictionary<string, int>(routing.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in routing)
            consumerCounts[kvp.Key] = kvp.Value.Length;

        return new DispatchStats(count, eventCounts, consumerCounts, providerNames);
    }

    /// <summary>Single-consumer convenience overload — used by standalone analyzer methods.</summary>
    public static DispatchStats Dispatch(TraceLog trace, ITraceEventConsumer consumer,
                                 Action<string>? progress = null)
        => Dispatch(trace, (IReadOnlyList<ITraceEventConsumer>)[consumer], progress);
}
