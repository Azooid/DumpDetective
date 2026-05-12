using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses SQL command events from a .nettrace / .etl trace.
///
/// Event sources consumed (in priority order):
///   Microsoft-AdoNet-SystemData           — BeginExecute / EndExecute (classic .NET Framework ADO.NET)
///   Microsoft.Data.SqlClient.EventSource  — SqlCommand.ExecuteXxx start/stop (.NET Core / .NET 5+)
///   System.Data.SqlClient.EventSource     — legacy SqlClient (full .NET Framework)
///   Microsoft-EntityFrameworkCore         — EF Core command execution
///
/// Required provider strings:
///   dotnet-trace:
///     --providers 'Microsoft-AdoNet-SystemData:0xFF:4,
///                  Microsoft.Data.SqlClient.EventSource:0xFF:4,
///                  System.Data.SqlClient.EventSource:0xFF:4,
///                  Microsoft-EntityFrameworkCore:0xFFFF:5'
///
///   PerfView:
///     /Providers:"Microsoft-AdoNet-SystemData,Microsoft.Data.SqlClient.EventSource,Microsoft-EntityFrameworkCore"
/// </summary>
public sealed class SqlTraceAnalyzer
{
    public SqlTraceData Analyze(string tracePath, int top = 100,
                                string? processFilter = null, double slowMs = 500)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter, slowMs);
        }
        catch (Exception ex)
        {
            return new SqlTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, 0, slowMs, 0, [], [], [], null, false);
        }
    }

    public SqlTraceData Analyze(TraceLog trace, string traceFileName, int top = 100,
                                string? processFilter = null, double slowMs = 500,
                                Action<string>? progress = null)
    {
        // Pending commands: correlation ID (or ThreadID) → start info
        var pending  = new Dictionary<string, (double StartMs, string Command, string Db)>(StringComparer.Ordinal);
        var commands = new List<SqlCommandEntry>();
        var queryAcc = new Dictionary<string, QueryAcc>(StringComparer.OrdinalIgnoreCase);
        var dbAcc    = new Dictionary<string, DbAcc>(StringComparer.OrdinalIgnoreCase);
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
                    progress($"{commands.Count:N0} SQL commands");
                    lastProgressMs = Environment.TickCount64;
                }
                if (processFilter is not null &&
                    !ev.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";

                // ── Command / Query start ──────────────────────────────────────
                if (IsCommandStart(evName))
                {
                    string key  = GetCorrelationKey(ev);
                    string db   = GetDatabase(ev);
                    string cmd  = GetCommandText(ev);

                    // When commandText is empty (trace not collected at Verbose level),
                    // build a descriptive label that includes db/server/objectId so the
                    // entry is not silently merged with all other empty-text commands.
                    if (cmd == "(no command text)")
                        cmd = MakeNoTextLabel(ev);

                    pending[key] = (ev.TimeStampRelativeMSec, cmd, db);
                    continue;
                }

                // ── Command / Query stop ───────────────────────────────────────
                if (IsCommandStop(evName))
                {
                    string key  = GetCorrelationKey(ev);
                    bool   isErr = IsErrorEvent(evName);

                    if (!pending.TryGetValue(key, out var entry))
                    {
                        // Unmatched stop — only track errors
                        if (isErr) commands.Add(new SqlCommandEntry(
                            "(unknown)", "(unknown)", 0, ev.TimeStampRelativeMSec, ev.ThreadID, true));
                        continue;
                    }

                    pending.Remove(key);
                    double durationMs = ev.TimeStampRelativeMSec - entry.StartMs;
                    if (durationMs < 0) durationMs = 0;

                    commands.Add(new SqlCommandEntry(
                        entry.Command, entry.Db, durationMs, entry.StartMs, ev.ThreadID, isErr));

                    // Accumulate by query text
                    string queryKey = NormalizeQuery(entry.Command);
                    if (!queryAcc.TryGetValue(queryKey, out var qacc))
                        queryAcc[queryKey] = qacc = new QueryAcc { Text = entry.Command };
                    qacc.Count++;
                    qacc.TotalMs += durationMs;
                    if (durationMs > qacc.MaxMs) qacc.MaxMs = durationMs;
                    if (isErr) qacc.Errors++;

                    // Accumulate by database
                    if (!dbAcc.TryGetValue(entry.Db, out var dacc))
                        dbAcc[entry.Db] = dacc = new DbAcc();
                    dacc.Count++;
                    dacc.TotalMs += durationMs;
                }
            }
        }
        catch (Exception ex)
        {
            return new SqlTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, 0, slowMs, 0, [], [], [], null, false);
        }

        if (commands.Count == 0)
        {
            return new SqlTraceData(
                $"{traceFileName}  |  No SQL events — see collection guidance",
                processFilter, 0, 0, 0, 0, 0, slowMs, 0, [], [], [], null, false);
        }

        double totalMs = commands.Sum(c => c.DurationMs);
        double maxMs   = commands.Max(c => c.DurationMs);
        double avgMs   = totalMs / commands.Count;
        int    errors  = commands.Count(c => c.IsError);
        var    slow    = ApplyLimit(commands.Where(c => c.DurationMs >= slowMs)
                                 .OrderByDescending(c => c.DurationMs), top)
                                 .ToList();

        // Three-tier query selection:
        //   Tier 1 — top `top` (100) by cumulative total time
        //   Tier 2 — top `top/2` (50) by worst single-execution time (surfaces one-off outliers)
        //   Tier 3 — top 20 additional unique patterns not already captured by tiers 1+2
        const int uniqueExtra = 20;
        var byTotal  = queryAcc.Values.OrderByDescending(q => q.TotalMs).Take(top).ToHashSet();
        var byMax    = queryAcc.Values.OrderByDescending(q => q.MaxMs).Take(Math.Max(1, top / 2)).ToHashSet();
        var covered  = new HashSet<QueryAcc>(byTotal.Concat(byMax));
        var byUnique = queryAcc.Values.Where(q => !covered.Contains(q)
                                               && !q.Text.StartsWith("(no SQL text", StringComparison.Ordinal))
                                      .OrderByDescending(q => q.TotalMs)
                                      .Take(uniqueExtra);
        // Final sort priority:
        //   1. Real SQL text first (no-SQL-text placeholders sink to bottom)
        //   2. Total time descending
        //   3. Max single execution descending
        var topQueries = covered.Concat(byUnique)
            .OrderBy(q => q.Text.StartsWith("(no SQL text", StringComparison.Ordinal) ? 1 : 0)
            .ThenByDescending(q => q.TotalMs)
            .ThenByDescending(q => q.MaxMs)
            .Select(q => new SqlQuerySummary(q.Text, q.Count, q.TotalMs, q.MaxMs,
                                             q.Count > 0 ? q.TotalMs / q.Count : 0, q.Errors))
            .ToList();

        var topDbs = ApplyLimit(dbAcc
            .OrderByDescending(kv => kv.Value.TotalMs), top)
            .Select(kv => new SqlDbSummary(kv.Key, kv.Value.Count, kv.Value.TotalMs,
                                           kv.Value.Count > 0 ? kv.Value.TotalMs / kv.Value.Count : 0))
            .ToList();

        // Duration timeline
        IReadOnlyList<double>? timeline = null;
        if (commands.Count > 1)
        {
            var perSecond = new Dictionary<int, double>();
            foreach (var c in commands)
            {
                int bucket = (int)(c.StartTimeMs / 1000.0);
                perSecond.TryGetValue(bucket, out double prev);
                perSecond[bucket] = prev + c.DurationMs;
            }
            int minB = int.MaxValue, maxB = int.MinValue;
            foreach (int k in perSecond.Keys) { if (k < minB) minB = k; if (k > maxB) maxB = k; }
            var tl = new double[maxB - minB + 1];
            foreach (var kv in perSecond) tl[kv.Key - minB] = kv.Value;
            timeline = tl;
        }

        // Format total as wall-clock-friendly string; the raw ms sum can easily exceed the
        // trace duration because SQL commands run concurrently across many threads.
        string totalFmt = totalMs >= 3_600_000 ? $"{totalMs / 3_600_000:F1} h"
                        : totalMs >= 60_000     ? $"{totalMs / 60_000:F1} min"
                        : totalMs >= 1_000      ? $"{totalMs / 1_000:F1} s"
                        : $"{totalMs:F0} ms";
        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {commands.Count:N0} commands  •  {totalFmt} total  •  {errors} errors";

        return new SqlTraceData(
            info, processFilter,
            commands.Count, errors, avgMs, maxMs, totalMs, slowMs, slow.Count,
            slow, topQueries, topDbs, timeline, HasData: true);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Event name matchers
    // ─────────────────────────────────────────────────────────────────────────
    private static bool IsCommandStart(string n) =>
        // Microsoft-AdoNet-SystemData/BeginExecute  (classic ADO.NET, .NET Framework)
        n.Contains("BeginExecute", StringComparison.OrdinalIgnoreCase) ||
        // Microsoft.Data.SqlClient.EventSource / System.Data.SqlClient.EventSource
        ((n.Contains("SqlCommand", StringComparison.OrdinalIgnoreCase) ||
          n.Contains("CommandExecuting", StringComparison.OrdinalIgnoreCase) ||
          n.Contains("CommandExecute", StringComparison.OrdinalIgnoreCase)) &&
         (n.Contains("Start", StringComparison.OrdinalIgnoreCase) ||
          n.Contains("Begin", StringComparison.OrdinalIgnoreCase) ||
          n.Contains("Executing", StringComparison.OrdinalIgnoreCase)));

    private static bool IsCommandStop(string n) =>
        // Microsoft-AdoNet-SystemData/EndExecute  (classic ADO.NET, .NET Framework)
        n.Contains("EndExecute", StringComparison.OrdinalIgnoreCase) ||
        // Microsoft.Data.SqlClient.EventSource / System.Data.SqlClient.EventSource
        ((n.Contains("SqlCommand", StringComparison.OrdinalIgnoreCase) ||
          n.Contains("CommandExecuted", StringComparison.OrdinalIgnoreCase) ||
          n.Contains("CommandExecute", StringComparison.OrdinalIgnoreCase)) &&
         (n.Contains("Stop", StringComparison.OrdinalIgnoreCase) ||
          n.Contains("End", StringComparison.OrdinalIgnoreCase) ||
          n.Contains("Executed", StringComparison.OrdinalIgnoreCase) ||
          n.Contains("Error", StringComparison.OrdinalIgnoreCase)));

    private static bool IsErrorEvent(string n) =>
        n.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("Fail", StringComparison.OrdinalIgnoreCase);

    // ─────────────────────────────────────────────────────────────────────────
    // Payload helpers
    // ─────────────────────────────────────────────────────────────────────────
    private static string GetCorrelationKey(TraceEvent ev)
    {
        // Microsoft-AdoNet-SystemData/BeginExecute: objectId field is an int (SqlCommand.GetHashCode())
        // Microsoft.Data.SqlClient: ActivityId (Guid) or ConnectionId (string)
        // Must handle int, long, Guid, and string — all converted to string for keying.
        foreach (string f in (string[])["objectId", "ObjectId", "ActivityId", "ConnectionId", "activityId"])
        {
            try
            {
                object? val = ev.PayloadByName(f);
                string? s = val switch
                {
                    string str when str.Length > 0 => str,
                    int    i                       => i.ToString(),
                    long   l                       => l.ToString(),
                    Guid   g when g != Guid.Empty  => g.ToString(),
                    _                              => null
                };
                if (s is not null) return s;
            }
            catch { /* field not present in this event */ }
        }
        return $"t{ev.ThreadID}";
    }

    private static string GetCommandText(TraceEvent ev)
    {
        // Try known field names in priority order.
        // Microsoft-AdoNet-SystemData manifest uses camelCase: commandText, dataSource, database.
        // Microsoft.Data.SqlClient uses PascalCase: CommandText.
        // Capture up to 500 chars — long stored-proc calls are useful to see in full.
        string? text = GetStringPayloadRaw(ev,
            "commandText", "CommandText", "command", "Query", "queryText", "text", "Statement");

        if (text is not null && text.Length > 0)
        {
            if (text.Length > 500) text = text[..500] + "…";
            return text;
        }

        // Last resort: enumerate every payload field and return the first non-empty string
        // value that looks like SQL. This surfaces command text even when the manifest uses
        // an unexpected field name (e.g. provider-specific naming).
        return GetFirstNonEmptyPayload(ev,
            exclude: ["objectId", "ObjectId", "dataSource", "DataSource",
                      "database", "Database", "compositeState", "sqlExceptionNumber",
                      "IsAsync", "isAsync"]);
    }

    /// <summary>
    /// Builds a display label for a command whose SQL text was not captured.
    /// Shows available metadata (server, database, objectId) so the row is still
    /// identifiable in the report and not silently merged with all other unknown commands.
    /// </summary>
    private static string MakeNoTextLabel(TraceEvent ev)
    {
        string? db  = GetStringPayloadRaw(ev, "database", "Database");
        string? srv = GetStringPayloadRaw(ev, "dataSource", "DataSource", "server");

        // Correlation key carries the objectId (SqlCommand hash) or thread id as fallback
        string key = GetCorrelationKey(ev);
        string idPart = key.StartsWith("t", StringComparison.Ordinal) && int.TryParse(key[1..], out _)
            ? $"thread={key[1..]}"   // thread-based fallback
            : $"id={key}";           // object hash

        var sb = new System.Text.StringBuilder("(no SQL text");
        if (db  is { Length: > 0 }) sb.Append($", db={db}");
        if (srv is { Length: > 0 }) sb.Append($", server={srv}");
        sb.Append($", {idPart})");
        return sb.ToString();
    }

    private static string GetDatabase(TraceEvent ev)
    {
        // Microsoft-AdoNet-SystemData: dataSource = server address, database = catalog name
        // Microsoft.Data.SqlClient: DataSource, Database
        string? db = GetStringPayloadRaw(ev,
            "database", "Database", "dataSource", "DataSource", "server");
        return db ?? "(unknown)";
    }

    private static string GetStringPayload(TraceEvent ev, params string[] fields)
        => GetStringPayloadRaw(ev, fields) ?? "(unknown)";

    private static string? GetStringPayloadRaw(TraceEvent ev, params string[] fields)
    {
        foreach (string f in fields)
        {
            try
            {
                object? val = ev.PayloadByName(f);
                if (val is string s && s.Length > 0) return s;
            }
            catch { /* field not present */ }
        }
        return null;
    }

    /// <summary>
    /// Walk every payload field in the event and return the first non-empty string value
    /// that is not in <paramref name="exclude"/>.
    /// Used as a last-resort fallback when field names are unknown (e.g. provider version differences).
    /// </summary>
    private static string GetFirstNonEmptyPayload(TraceEvent ev, string[] exclude)
    {
        try
        {
            string[] names = ev.PayloadNames;
            for (int i = 0; i < names.Length; i++)
            {
                bool skip = false;
                foreach (string ex in exclude)
                    if (string.Equals(names[i], ex, StringComparison.OrdinalIgnoreCase))
                    { skip = true; break; }
                if (skip) continue;

                try
                {
                    object? val = ev.PayloadValue(i);
                    if (val is string s && s.Length > 0)
                    {
                        if (s.Length > 500) s = s[..500] + "…";
                        return s;
                    }
                }
                catch { /* skip bad payload slot */ }
            }
        }
        catch { /* PayloadNames not available */ }
        return "(no command text)";
    }

    // Normalize query for grouping: replace literal values with placeholders
    private static string NormalizeQuery(string sql)
    {
        if (sql.Length > 150) sql = sql[..150];
        return sql.Trim();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies a row limit: <c>n &gt; 0</c> takes exactly <c>n</c> rows;
    /// <c>n == 0</c> returns all rows (no limit).
    /// </summary>
    private static IEnumerable<T> ApplyLimit<T>(IEnumerable<T> source, int n)
        => n > 0 ? source.Take(n) : source;

    // ─────────────────────────────────────────────────────────────────────────
    // Accumulator types (heap-allocated once per unique key)
    // ─────────────────────────────────────────────────────────────────────────
    private sealed class QueryAcc
    {
        public string Text  = "";
        public int    Count;
        public double TotalMs;
        public double MaxMs;
        public int    Errors;
    }

    private sealed class DbAcc
    {
        public int    Count;
        public double TotalMs;
    }
}
