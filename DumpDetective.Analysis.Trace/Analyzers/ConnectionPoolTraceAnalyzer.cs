using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using static DumpDetective.Core.Tracing.TraceEventKind;


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

    private static readonly Dictionary<string, byte> EvKind = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly Dictionary<int, string> OpenByThread = new();
        internal readonly Dictionary<string, DbAcc> DbAccs = new(StringComparer.OrdinalIgnoreCase);
        internal int CurrentOpen;
        internal int PeakOpen;
        internal readonly Dictionary<int, int> PerSecond = new();
        internal int TotalOpens;
        internal int TotalCloses;

        public void Consume(TraceEvent ev, in TraceEventMeta meta, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            // EF Core and SqlClient can fire connection events via DiagnosticSource.
            if (meta.ProviderName.Contains("DiagnosticSource", StringComparison.OrdinalIgnoreCase))
            {
                ConsumeConnectionDiagnosticSource(ev, timestampMs, threadId);
                return;
            }

            if (!EvKind.TryGetValue(meta.EventName, out byte kind))
                EvKind[meta.EventName] = kind =
                    meta.EventName.EndsWith("ConnectionOpen",  StringComparison.OrdinalIgnoreCase) ||
                    (meta.EventName.Contains("SqlConnection",  StringComparison.OrdinalIgnoreCase) &&
                     meta.EventName.Contains("Open",           StringComparison.OrdinalIgnoreCase)) ? (byte)1 :
                    meta.EventName.EndsWith("ConnectionClose", StringComparison.OrdinalIgnoreCase) ||
                    (meta.EventName.Contains("SqlConnection",  StringComparison.OrdinalIgnoreCase) &&
                     meta.EventName.Contains("Close",          StringComparison.OrdinalIgnoreCase)) ? (byte)2 :
                    (byte)0;
            if (kind == 0) return;
            bool isOpen  = kind == 1;
            bool isClose = kind == 2;

            string db = SafeStr(ev, "DataSource");
            if (db.Length == 0) db = SafeStr(ev, "Database");
            if (db.Length == 0) db = SafeStr(ev, "ServerVersion");
            if (db.Length == 0) db = "(unknown)";

            if (!DbAccs.TryGetValue(db, out var acc))
                DbAccs[db] = acc = new DbAcc();

            if (isOpen)
            {
                TotalOpens++;
                CurrentOpen++;
                if (CurrentOpen > PeakOpen) PeakOpen = CurrentOpen;
                OpenByThread[threadId] = db;
                acc.Opens++;

                int bucket = (int)(timestampMs / 1000.0);
                PerSecond.TryGetValue(bucket, out int pv);
                PerSecond[bucket] = Math.Max(pv, CurrentOpen);
                if (CurrentOpen > acc.PeakConcurrent) acc.PeakConcurrent = CurrentOpen;
            }
            else
            {
                TotalCloses++;
                if (CurrentOpen > 0) CurrentOpen--;
                OpenByThread.Remove(threadId);
                acc.Closes++;

                int bucket = (int)(timestampMs / 1000.0);
                PerSecond.TryGetValue(bucket, out int pv);
                PerSecond[bucket] = Math.Max(pv, CurrentOpen);
            }
        }

        public bool WantsEvent(in TraceEventMeta meta)
        {
            // EF Core fires Database.Connection.ConnectionOpening/ConnectionClosed via DiagnosticSource.
            if (meta.ProviderName.Contains("DiagnosticSource", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!EvKind.TryGetValue(meta.EventName, out byte v))
                EvKind[meta.EventName] = v = meta.Kind switch
                {
                    _ when meta.Kind == SqlConnectionOpen => 1,
                    _ when meta.Kind == SqlConnectionClose => 2,
                    _ when meta.IsKnown => 0,
                    _ => meta.EventName.EndsWith("ConnectionOpen", StringComparison.OrdinalIgnoreCase) ||
                         (meta.EventName.Contains("SqlConnection", StringComparison.OrdinalIgnoreCase) &&
                          meta.EventName.Contains("Open",          StringComparison.OrdinalIgnoreCase)) ? (byte)1
                       : meta.EventName.EndsWith("ConnectionClose", StringComparison.OrdinalIgnoreCase) ||
                         (meta.EventName.Contains("SqlConnection", StringComparison.OrdinalIgnoreCase) &&
                          meta.EventName.Contains("Close",         StringComparison.OrdinalIgnoreCase)) ? (byte)2
                       : (byte)0
                };
            return v != 0;
        }

        public void OnComplete() { }

        private void ConsumeConnectionDiagnosticSource(TraceEvent ev, double timestampMs, int threadId)
        {
            string sourceName  = SafeStr(ev, "SourceName");
            string innerEvName = SafeStr(ev, "EventName");
            string arguments   = SafeStr(ev, "Arguments");

            // Handle EF Core connection events (SourceName contains "EntityFrameworkCore") and
            // Microsoft.Data.SqlClient via DiagnosticSource.
            if (!sourceName.Contains("EntityFramework", StringComparison.OrdinalIgnoreCase) &&
                !sourceName.Contains("SqlClient",       StringComparison.OrdinalIgnoreCase))
                return;

            bool isOpen  = innerEvName.EndsWith("ConnectionOpening", StringComparison.OrdinalIgnoreCase) ||
                           innerEvName.EndsWith("ConnectionOpened",   StringComparison.OrdinalIgnoreCase);
            bool isClose = innerEvName.EndsWith("ConnectionClosed",   StringComparison.OrdinalIgnoreCase) ||
                           innerEvName.EndsWith("ConnectionClosing",   StringComparison.OrdinalIgnoreCase);
            if (!isOpen && !isClose) return;

            string db = ExtractArgValue(arguments, "Database") ?? "";
            if (db.Length == 0) db = ExtractArgValue(arguments, "DataSource") ?? "";
            if (db.Length == 0) db = sourceName;

            if (!DbAccs.TryGetValue(db, out var acc))
                DbAccs[db] = acc = new DbAcc();

            if (isOpen)
            {
                TotalOpens++;
                CurrentOpen++;
                if (CurrentOpen > PeakOpen) PeakOpen = CurrentOpen;
                OpenByThread[threadId] = db;
                acc.Opens++;

                int bucket = (int)(timestampMs / 1000.0);
                PerSecond.TryGetValue(bucket, out int pv);
                PerSecond[bucket] = Math.Max(pv, CurrentOpen);
                if (CurrentOpen > acc.PeakConcurrent) acc.PeakConcurrent = CurrentOpen;
            }
            else
            {
                TotalCloses++;
                if (CurrentOpen > 0) CurrentOpen--;
                OpenByThread.Remove(threadId);
                acc.Closes++;

                int bucket = (int)(timestampMs / 1000.0);
                PerSecond.TryGetValue(bucket, out int pv);
                PerSecond[bucket] = Math.Max(pv, CurrentOpen);
            }
        }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null)
        => new Consumer(processFilter);

    public ConnectionPoolTraceData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 20,
                                                string? processFilter = null)
    {
        var c = (Consumer)consumer;
        if (c.TotalOpens == 0 && c.TotalCloses == 0)
        {
            return new ConnectionPoolTraceData(
                $"{traceFileName}  |  0 SqlConnection events — collect with " +
                "--providers 'Microsoft.Data.SqlClient.EventSource:0xFF:4,System.Data.SqlClient.EventSource:0xFF:4'",
                processFilter, 0, 0, 0, 0, [], null, false);
        }

        int leaked = c.TotalOpens - c.TotalCloses;
        if (leaked < 0) leaked = 0;

        var topDbs = c.DbAccs
            .OrderByDescending(kv => kv.Value.Opens)
            .Take(top)
            .Select(kv => new ConnectionPoolDbSummary(kv.Key,
                kv.Value.Opens, kv.Value.Closes,
                Math.Max(0, kv.Value.Opens - kv.Value.Closes),
                kv.Value.PeakConcurrent))
            .ToList();

        var timeline = BuildTimeline(c.PerSecond);

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {c.TotalOpens:N0} opens  •  {c.TotalCloses:N0} closes  •  peak {c.PeakOpen}";

        return new ConnectionPoolTraceData(info, processFilter,
            c.TotalOpens, c.TotalCloses, c.PeakOpen, leaked, topDbs, timeline, HasData: true);
    }

    public ConnectionPoolTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
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
            return new ConnectionPoolTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, [], null, false);
        }
    }


    private sealed class DbAcc
    {
        public int Opens;
        public int Closes;
        public double PeakConcurrent;
    }
}
