using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses Windows Kernel Process/Start and Process/Stop events to detect
/// process restarts, crashes, and abnormal exits.
/// </summary>
public sealed class ProcessLifecycleAnalyzer
{
    private const double RestartWindowMs = 30_000; // 30 s: stop→start = restart

    public ProcessLifecycleData Analyze(string tracePath, int top = 20, string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new ProcessLifecycleData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, [], [], false);
        }
    }

    public ProcessLifecycleData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                         string? processFilter = null)
    {
        var events = new List<ProcessEvent>();
        // Track last stop per process name for restart detection
        var lastStop = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        int restarts = 0, abnormal = 0;

        try
        {
            foreach (var ev in trace.Events)
            {
                string evName = ev.EventName ?? "";
                bool isStart =
                    evName.EndsWith("Process/Start",  StringComparison.OrdinalIgnoreCase) ||
                    evName.EndsWith("ProcessStart",   StringComparison.OrdinalIgnoreCase) ||
                    evName.Contains("ProcessStart/Start", StringComparison.OrdinalIgnoreCase);
                bool isStop =
                    evName.EndsWith("Process/Stop",   StringComparison.OrdinalIgnoreCase) ||
                    evName.EndsWith("ProcessStop",    StringComparison.OrdinalIgnoreCase) ||
                    evName.Contains("ProcessStop/Stop",  StringComparison.OrdinalIgnoreCase);

                if (!isStart && !isStop) continue;

                string procName = SafeStr(ev, "ImageFileName");
                if (procName.Length == 0) procName = ev.ProcessName ?? "";
                if (procName.Length == 0) procName = "(unknown)";

                // Apply process filter if provided
                if (processFilter is not null &&
                    !procName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                int? exitCode = null;
                if (isStop)
                {
                    try { exitCode = (int)Convert.ChangeType(ev.PayloadByName("ExitCode"), typeof(int)); } catch { }
                }

                string eventType = isStart ? "Start" : "Stop";
                events.Add(new ProcessEvent(procName, ev.ProcessID, eventType,
                    ev.TimeStampRelativeMSec, exitCode));

                if (isStart && lastStop.TryGetValue(procName, out double stopMs))
                {
                    if (ev.TimeStampRelativeMSec - stopMs < RestartWindowMs)
                        restarts++;
                    lastStop.Remove(procName);
                }

                if (isStop)
                {
                    lastStop[procName] = ev.TimeStampRelativeMSec;
                    // Abnormal exit: non-zero exit code or specific codes (0xC0000005 = access violation)
                    if (exitCode.HasValue && exitCode != 0)
                        abnormal++;
                }
            }
        }
        catch (Exception ex)
        {
            return new ProcessLifecycleData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, [], [], false);
        }

        if (events.Count == 0)
        {
            return new ProcessLifecycleData(
                $"{traceFileName}  |  0 process lifecycle events — collect with " +
                "--providers 'Microsoft-Windows-Kernel-Process:0x10:4' or use PerfView with Kernel Process events",
                processFilter, 0, 0, 0, 0, [], [], false);
        }

        int totalStarts = events.Count(e => e.EventType == "Start");
        int totalStops  = events.Count(e => e.EventType == "Stop");

        var groups = events
            .GroupBy(e => e.ProcessName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(top)
            .Select(g =>
            {
                int s  = g.Count(e => e.EventType == "Start");
                int st = g.Count(e => e.EventType == "Stop");
                int ab = g.Count(e => e.EventType == "Stop" && e.ExitCode.HasValue && e.ExitCode != 0);
                // Approximate restarts per group
                int r  = Math.Max(0, Math.Min(s, st) - (ab > 0 ? 0 : 0));
                return new ProcessGroupSummary(g.Key, s, st, r, ab);
            })
            .ToList();

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  filter: {processFilter}" : "") +
                      $"  |  {totalStarts} starts  •  {totalStops} stops" +
                      (restarts > 0 ? $"  •  {restarts} restarts" : "") +
                      (abnormal > 0 ? $"  •  {abnormal} abnormal exits" : "");

        return new ProcessLifecycleData(info, processFilter,
            totalStarts, totalStops, restarts, abnormal,
            events.OrderBy(e => e.TimeMs).ToList(), groups, HasData: true);
    }

    private static string SafeStr(TraceEvent ev, string field)
    {
        try { return ev.PayloadByName(field)?.ToString() ?? ""; } catch { return ""; }
    }
}
