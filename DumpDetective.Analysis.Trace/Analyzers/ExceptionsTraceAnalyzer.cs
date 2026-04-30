using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses first-chance exception events from a .nettrace / .etl / .etl.zip trace.
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

    public ExceptionsTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                        string? processFilter = null)
    {
        var byType  = new Dictionary<string, TypeAcc>(StringComparer.Ordinal);
        var events  = new List<ExceptionEvent>();

        try
        {
            foreach (var ev in trace.Events)
            {
                if (processFilter is not null &&
                    !ev.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";

                bool isEx =
                    evName.EndsWith("Exception/Start",    StringComparison.OrdinalIgnoreCase) ||
                    evName.EndsWith("ExceptionThrown",     StringComparison.OrdinalIgnoreCase) ||
                    evName.EndsWith("Exception",           StringComparison.OrdinalIgnoreCase) ||
                    evName.IndexOf("ExceptionCatchStart", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!isEx) continue;

                string exType = SafeStr(ev, "ExceptionType");
                if (exType.Length == 0) exType = SafeStr(ev, "Type");
                if (exType.Length == 0) exType = "(unknown)";

                string msg     = SafeStr(ev, "ExceptionMessage");
                if (msg.Length == 0) msg = SafeStr(ev, "Message");
                string frame   = TopUserFrame(ev);

                events.Add(new ExceptionEvent(exType, msg, ev.TimeStampRelativeMSec, frame, ev.ThreadID));

                if (!byType.TryGetValue(exType, out var acc))
                    byType[exType] = acc = new TypeAcc(msg, frame);
                acc.Count++;
            }
        }
        catch (Exception ex)
        {
            return new ExceptionsTraceData($"Failed: {ex.Message}", processFilter, 0, 0, [], []);
        }

        if (events.Count == 0)
            return new ExceptionsTraceData(
                $"{traceFileName}  |  0 exception events — collect with --providers Microsoft-Windows-DotNETRuntime:0x8014:5",
                processFilter, 0, 0, [], []);

        var topTypes = byType
            .OrderByDescending(kv => kv.Value.Count)
            .Take(top)
            .Select(kv => new ExceptionTypeSummary(kv.Key, kv.Value.Count,
                                                    Truncate(kv.Value.FirstMsg, 120),
                                                    kv.Value.TopFrame))
            .ToList();

        var recentEvents = events
            .OrderByDescending(e => e.TimeMs)
            .Take(top)
            .ToList();

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {events.Count:N0} exceptions  •  {byType.Count} unique types";

        return new ExceptionsTraceData(info, processFilter,
            events.Count, byType.Count, topTypes, recentEvents);
    }

    private static string SafeStr(TraceEvent ev, string field)
    {
        try { return ev.PayloadByName(field)?.ToString() ?? ""; } catch { return ""; }
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

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private sealed class TypeAcc(string firstMsg, string topFrame)
    {
        public int    Count    = 1;
        public string FirstMsg = firstMsg;
        public string TopFrame = topFrame;
    }
}
