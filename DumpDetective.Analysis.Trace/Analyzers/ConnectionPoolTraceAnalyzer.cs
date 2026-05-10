using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses SqlConnection Open/Close events to detect DB connection pool exhaustion,
/// connection leaks (opens without matching closes), and connection churn.
/// </summary>
public sealed class ConnectionPoolTraceAnalyzer
{
    public ConnectionPoolTraceData Analyze(string tracePath, int top = 20,
                                            string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new ConnectionPoolTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, [], null, false);
        }
    }

    public ConnectionPoolTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                            string? processFilter = null)
    {
        // Track open connections by thread — key: ThreadID, value: database
        var openByThread = new Dictionary<int, string>();

        // Per-db accumulator
        var dbAcc = new Dictionary<string, DbAcc>(StringComparer.OrdinalIgnoreCase);

        // Per-second concurrent open count
        int currentOpen = 0;
        int peakOpen = 0;
        var perSecond = new Dictionary<int, int>();

        int totalOpens = 0, totalCloses = 0;

        try
        {
            foreach (var ev in trace.Events)
            {
                if (processFilter is not null &&
                    !ev.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";

                bool isOpen  = evName.EndsWith("ConnectionOpen",  StringComparison.OrdinalIgnoreCase) ||
                               (evName.Contains("SqlConnection", StringComparison.OrdinalIgnoreCase) &&
                                evName.Contains("Open",          StringComparison.OrdinalIgnoreCase));
                bool isClose = evName.EndsWith("ConnectionClose", StringComparison.OrdinalIgnoreCase) ||
                               (evName.Contains("SqlConnection", StringComparison.OrdinalIgnoreCase) &&
                                evName.Contains("Close",         StringComparison.OrdinalIgnoreCase));

                if (!isOpen && !isClose) continue;

                string db = SafeStr(ev, "DataSource");
                if (db.Length == 0) db = SafeStr(ev, "Database");
                if (db.Length == 0) db = SafeStr(ev, "ServerVersion");
                if (db.Length == 0) db = "(unknown)";

                if (!dbAcc.TryGetValue(db, out var acc))
                    dbAcc[db] = acc = new DbAcc();

                if (isOpen)
                {
                    totalOpens++;
                    currentOpen++;
                    if (currentOpen > peakOpen) peakOpen = currentOpen;
                    openByThread[ev.ThreadID] = db;
                    acc.Opens++;

                    // Track per second
                    int bucket = (int)(ev.TimeStampRelativeMSec / 1000.0);
                    perSecond.TryGetValue(bucket, out int pv);
                    perSecond[bucket] = Math.Max(pv, currentOpen);
                    if (currentOpen > acc.PeakConcurrent) acc.PeakConcurrent = currentOpen;
                }
                else
                {
                    totalCloses++;
                    if (currentOpen > 0) currentOpen--;
                    openByThread.Remove(ev.ThreadID);
                    acc.Closes++;

                    int bucket = (int)(ev.TimeStampRelativeMSec / 1000.0);
                    perSecond.TryGetValue(bucket, out int pv);
                    perSecond[bucket] = Math.Max(pv, currentOpen);
                }
            }
        }
        catch (Exception ex)
        {
            return new ConnectionPoolTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, [], null, false);
        }

        if (totalOpens == 0 && totalCloses == 0)
        {
            return new ConnectionPoolTraceData(
                $"{traceFileName}  |  0 SqlConnection events — collect with " +
                "--providers 'Microsoft.Data.SqlClient.EventSource:0xFF:4,System.Data.SqlClient.EventSource:0xFF:4'",
                processFilter, 0, 0, 0, 0, [], null, false);
        }

        int leaked = totalOpens - totalCloses;
        if (leaked < 0) leaked = 0;

        var topDbs = dbAcc
            .OrderByDescending(kv => kv.Value.Opens)
            .Take(top)
            .Select(kv => new ConnectionPoolDbSummary(kv.Key,
                kv.Value.Opens, kv.Value.Closes,
                Math.Max(0, kv.Value.Opens - kv.Value.Closes),
                kv.Value.PeakConcurrent))
            .ToList();

        IReadOnlyList<double>? timeline = null;
        if (perSecond.Count > 1)
        {
            int minB = perSecond.Keys.Min(), maxB = perSecond.Keys.Max();
            var tl = new double[maxB - minB + 1];
            foreach (var kv in perSecond) tl[kv.Key - minB] = kv.Value;
            timeline = tl;
        }

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {totalOpens:N0} opens  •  {totalCloses:N0} closes  •  peak {peakOpen}";

        return new ConnectionPoolTraceData(info, processFilter,
            totalOpens, totalCloses, peakOpen, leaked, topDbs, timeline, HasData: true);
    }

    private static string SafeStr(TraceEvent ev, string field)
    {
        try { return ev.PayloadByName(field)?.ToString() ?? ""; } catch { return ""; }
    }

    private sealed class DbAcc
    {
        public int Opens;
        public int Closes;
        public double PeakConcurrent;
    }
}
