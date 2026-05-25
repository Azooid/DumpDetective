using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using static DumpDetective.Analysis.Trace.TraceEventHelpers;
using static DumpDetective.Core.Tracing.TraceEventKind;

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

    private static readonly Dictionary<string, byte> EvKind = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Consumer(string? processFilter, double slowMs) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly double SlowMs = slowMs;
        internal readonly Dictionary<string, (double StartMs, string Command, string Db)> Pending = new(StringComparer.Ordinal);
        internal readonly List<SqlCommandEntry> Commands = new();
        internal readonly Dictionary<string, QueryAcc> QueryAcc = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, DbAcc> DbAcc = new(StringComparer.OrdinalIgnoreCase);
        // EF Core DiagnosticSource events are collected separately so they can be discarded when
        // native SqlClient EventSource events are also present (both represent the same commands).
        internal bool HasNativeSqlEvents;
        internal readonly List<SqlCommandEntry> _diagCmds = [];
        internal readonly Dictionary<string, QueryAcc> _diagQueryAcc = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, DbAcc> _diagDbAcc = new(StringComparer.OrdinalIgnoreCase);

        public void Consume(TraceEvent ev, in TraceEventMeta meta, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            // Microsoft-Diagnostics-DiagnosticSource/Event carries EF Core events inside
            // its payload; the outer event name is always "Event" so normal classification
            // cannot distinguish them.  Route them to the dedicated handler.
            if (meta.ProviderName.Contains("DiagnosticSource", StringComparison.OrdinalIgnoreCase))
            {
                ConsumeDiagnosticSourceEvent(ev, timestampMs, threadId);
                return;
            }

            if (!EvKind.TryGetValue(meta.EventName, out byte kind))
                EvKind[meta.EventName] = kind = ComputeSqlKind(meta.EventName);
            if (kind == 0) return;

            if (kind == 1)
            {
                string key  = GetCorrelationKey(ev);
                string db   = GetDatabase(ev);
                string cmd  = GetCommandText(ev);
                if (cmd == "(no command text)")
                    cmd = MakeNoTextLabel(ev);
                Pending[key] = (timestampMs, cmd, db);
                HasNativeSqlEvents = true;
                return;
            }

            if (kind == 2 || kind == 3)
            {
                string key   = GetCorrelationKey(ev);
                bool   isErr = (kind == 3);

                if (!Pending.TryGetValue(key, out var entry))
                {
                    if (isErr) Commands.Add(new SqlCommandEntry(
                        "(unknown)", "(unknown)", 0, timestampMs, threadId, true));
                    return;
                }

                Pending.Remove(key);
                double durationMs = timestampMs - entry.StartMs;
                if (durationMs < 0) durationMs = 0;

                Commands.Add(new SqlCommandEntry(
                    entry.Command, entry.Db, durationMs, entry.StartMs, threadId, isErr));

                string queryKey = NormalizeQuery(entry.Command);
                if (!QueryAcc.TryGetValue(queryKey, out var qacc))
                    QueryAcc[queryKey] = qacc = new QueryAcc { Text = entry.Command };
                qacc.Count++;
                qacc.TotalMs += durationMs;
                if (durationMs > qacc.MaxMs) qacc.MaxMs = durationMs;
                if (isErr) qacc.Errors++;

                if (!DbAcc.TryGetValue(entry.Db, out var dacc))
                    DbAcc[entry.Db] = dacc = new DbAcc();
                dacc.Count++;
                dacc.TotalMs += durationMs;
            }
        }

        public bool WantsEvent(in TraceEventMeta meta)
        {
            // DiagnosticSource outer events all share EventName="Event" and cannot be
            // classified from the name alone; let them through and filter in Consume().
            if (meta.ProviderName.Contains("DiagnosticSource", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!EvKind.TryGetValue(meta.EventName, out byte v))
                EvKind[meta.EventName] = v = meta.Kind switch
                {
                    _ when meta.Kind == SqlCommandStart => 1,
                    _ when meta.Kind == SqlCommandStop => ComputeSqlKind(meta.EventName),
                    _ when meta.IsKnown => 0,
                    _ => ComputeSqlKind(meta.EventName)
                };
            return v != 0;
        }

        /// <summary>
        /// Handles EF Core command events bridged through Microsoft-Diagnostics-DiagnosticSource.
        /// Matches on SourceName (contains "EntityFramework") and the inner EventName suffix
        /// (CommandExecuting / CommandExecuted / CommandError).
        /// </summary>
        private void ConsumeDiagnosticSourceEvent(TraceEvent ev, double timestampMs, int threadId)
        {
            string? sourceName = GetStringPayloadRaw(ev, "SourceName", "sourceName");
            if (sourceName is null ||
                !sourceName.Contains("EntityFramework", StringComparison.OrdinalIgnoreCase))
                return;

            string? innerEventName = GetStringPayloadRaw(ev, "EventName", "eventName");
            if (innerEventName is null) return;

            byte kind;
            if (innerEventName.EndsWith("CommandExecuting", StringComparison.OrdinalIgnoreCase))
                kind = 1;
            else if (innerEventName.EndsWith("CommandExecuted", StringComparison.OrdinalIgnoreCase))
                kind = 2;
            else if (innerEventName.EndsWith("CommandError", StringComparison.OrdinalIgnoreCase))
                kind = 3;
            else
                return;

            string key = GetEfCorrelationKey(ev, threadId);

            if (kind == 1)
            {
                string cmd = GetEfCommandText(ev);
                string db  = GetEfDatabase(ev);
                Pending[key] = (timestampMs, cmd, db);
                return;
            }

            bool isErr = kind == 3;
            if (!Pending.TryGetValue(key, out var entry))
            {
                if (isErr) _diagCmds.Add(new SqlCommandEntry(
                    "(unknown)", "(unknown)", 0, timestampMs, threadId, true));
                return;
            }

            Pending.Remove(key);
            double durationMs = Math.Max(0, timestampMs - entry.StartMs);
            _diagCmds.Add(new SqlCommandEntry(
                entry.Command, entry.Db, durationMs, entry.StartMs, threadId, isErr));

            string queryKey = NormalizeQuery(entry.Command);
            if (!_diagQueryAcc.TryGetValue(queryKey, out var qacc))
                _diagQueryAcc[queryKey] = qacc = new QueryAcc { Text = entry.Command };
            qacc.Count++;
            qacc.TotalMs += durationMs;
            if (durationMs > qacc.MaxMs) qacc.MaxMs = durationMs;
            if (isErr) qacc.Errors++;

            if (!_diagDbAcc.TryGetValue(entry.Db, out var dacc))
                _diagDbAcc[entry.Db] = dacc = new DbAcc();
            dacc.Count++;
            dacc.TotalMs += durationMs;
        }

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null, double slowMs = 500)
        => new Consumer(processFilter, slowMs);

    public SqlTraceData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 100,
                                     string? processFilter = null)
    {
        var c = (Consumer)consumer;
        // Prefer native SqlClient events over EF Core DiagnosticSource to avoid double-counting.
        // Both represent the same SQL commands; native events carry real SQL text and DB names.
        var commands = (c.HasNativeSqlEvents || c._diagCmds.Count == 0) ? c.Commands : c._diagCmds;
        var queryAcc = (c.HasNativeSqlEvents || c._diagQueryAcc.Count == 0) ? c.QueryAcc : c._diagQueryAcc;
        var dbAcc    = (c.HasNativeSqlEvents || c._diagDbAcc.Count == 0)    ? c.DbAcc    : c._diagDbAcc;

        if (commands.Count == 0)
        {
            return new SqlTraceData(
                $"{traceFileName}  |  No SQL events — see collection guidance",
                processFilter, 0, 0, 0, 0, 0, c.SlowMs, 0, [], [], [], null, false);
        }

        double totalMs = commands.Sum(cmd => cmd.DurationMs);
        double maxMs   = commands.Max(cmd => cmd.DurationMs);
        double avgMs   = totalMs / commands.Count;
        int    errors  = commands.Count(cmd => cmd.IsError);
        var    slow    = ApplyLimit(commands.Where(cmd => cmd.DurationMs >= c.SlowMs)
                                 .OrderByDescending(cmd => cmd.DurationMs), top)
                                 .ToList();

        const int uniqueExtra = 20;
        var byTotal  = queryAcc.Values.OrderByDescending(q => q.TotalMs).Take(top).ToHashSet();
        var byMax    = queryAcc.Values.OrderByDescending(q => q.MaxMs).Take(Math.Max(1, top / 2)).ToHashSet();
        var covered  = new HashSet<QueryAcc>(byTotal.Concat(byMax));

        // If many "(no SQL text)" entries dominated the top lists, widen the real-query
        // window so genuine SQL doesn't get crowded out.
        //   ≥ 10 unknown in covered → uniqueExtra = 100
        //    ≥ 3 unknown in covered → uniqueExtra = 50
        //   otherwise              → uniqueExtra = 20 (default)
        int unknownInCovered = 0;
        foreach (var q in covered)
            if (q.Text.StartsWith("(no SQL text", StringComparison.Ordinal)) unknownInCovered++;
        int effectiveUniqueExtra = unknownInCovered >= 10 ? 100 : unknownInCovered >= 3 ? 50 : uniqueExtra;
        var byUnique = queryAcc.Values.Where(q => !covered.Contains(q)
                                               && !q.Text.StartsWith("(no SQL text", StringComparison.Ordinal))
                                      .OrderByDescending(q => q.TotalMs)
                                      .Take(effectiveUniqueExtra);
        var topQueries = covered.Concat(byUnique)
            .OrderBy(q => q.Text.StartsWith("(no SQL text", StringComparison.Ordinal) ? 1 : 0)
            .ThenByDescending(q => q.TotalMs)
            .ThenByDescending(q => q.MaxMs)
            .Select(q => new SqlQuerySummary(q.Text, q.Count, q.TotalMs, q.MaxMs,
                                             q.Count > 0 ? q.TotalMs / q.Count : 0, q.Errors,
                                             DetectOrm(q.Text)))
            .ToList();

        var topDbs = ApplyLimit(dbAcc
            .OrderByDescending(kv => kv.Value.TotalMs), top)
            .Select(kv => new SqlDbSummary(kv.Key, kv.Value.Count, kv.Value.TotalMs,
                                           kv.Value.Count > 0 ? kv.Value.TotalMs / kv.Value.Count : 0))
            .ToList();

        IReadOnlyList<double>? timeline = null;
        if (commands.Count > 1)
        {
            var perSecond = new Dictionary<int, double>();
            foreach (var cmd in commands)
            {
                int bucket = (int)(cmd.StartTimeMs / 1000.0);
                perSecond.TryGetValue(bucket, out double prev);
                perSecond[bucket] = prev + cmd.DurationMs;
            }
            int minB = int.MaxValue, maxB2 = int.MinValue;
            foreach (int k in perSecond.Keys) { if (k < minB) minB = k; if (k > maxB2) maxB2 = k; }
            var tl = new double[maxB2 - minB + 1];
            foreach (var kv in perSecond) tl[kv.Key - minB] = kv.Value;
            timeline = tl;
        }

        string totalFmt = totalMs >= 3_600_000 ? $"{totalMs / 3_600_000:F1} h"
                        : totalMs >= 60_000     ? $"{totalMs / 60_000:F1} min"
                        : totalMs >= 1_000      ? $"{totalMs / 1_000:F1} s"
                        : $"{totalMs:F0} ms";
        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {commands.Count:N0} commands  •  {totalFmt} total  •  {errors} errors";

        return new SqlTraceData(
            info, processFilter,
            commands.Count, errors, avgMs, maxMs, totalMs, c.SlowMs, slow.Count,
            slow, topQueries, topDbs, timeline, HasData: true);
    }

    public SqlTraceData Analyze(TraceLog trace, string traceFileName, int top = 100,
                                string? processFilter = null, double slowMs = 500,
                                Action<string>? progress = null)
    {
        try
        {
            var c = CreateConsumer(processFilter, slowMs);
            TraceEventDispatcher.Dispatch(trace, c,
                progress);
            return BuildResult(c, traceFileName, top, processFilter);
        }
        catch (Exception ex)
        {
            return new SqlTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, 0, slowMs, 0, [], [], [], null, false);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Event name matchers
    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>Classify event name once: 0=skip, 1=start, 2=stop, 3=error-stop.</summary>
    private static byte ComputeSqlKind(string n)
    {
        if (IsCommandStart(n)) return 1;
        if (IsCommandStop(n))  return IsErrorEvent(n) ? (byte)3 : (byte)2;
        return 0;
    }

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

    // ─────────────────────────────────────────────────────────────────────────
    // DiagnosticSource / EF Core payload helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a correlation key for an EF Core DiagnosticSource command event.
    /// Extracts CommandId from the Arguments StructValue[] via String indexer; falls back to thread.
    /// </summary>
    private static string GetEfCorrelationKey(TraceEvent ev, int threadId)
    {
        // Arguments is StructValue[] — use the String indexer helper (not ExtractArgValue which
        // expects the old [Key->"value"] string format that DiagnosticSource no longer emits).
        string? cmdId = ExtractStructValueField(ev, "CommandId")
                     ?? ExtractStructValueField(ev, "commandId");
        if (cmdId is { Length: > 0 }) return "efcmd:" + cmdId;
        return $"t{threadId}";
    }

    /// <summary>
    /// Tries to extract SQL command text from the EF Core DiagnosticSource Arguments StructValue[].
    /// The DiagnosticSource ETW bridge serialises objects as their type name (not field values), so
    /// CommandText is not directly available. A descriptive fallback label is returned instead,
    /// using the DbContext class name and command source when available.
    /// </summary>
    private static string GetEfCommandText(TraceEvent ev)
    {
        // Try CommandText via StructValue (would work if ever populated as a string).
        string? text = ExtractStructValueField(ev, "CommandText")
                    ?? ExtractStructValueField(ev, "commandText");
        if (text is { Length: > 0 } &&
            !text.Contains("SqlCommand",    StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("NpgsqlCommand", StringComparison.OrdinalIgnoreCase) &&
            !text.EndsWith("Command",       StringComparison.OrdinalIgnoreCase))
            return text.Length > 500 ? text[..500] + "\u2026" : text;

        // SQL text not available via DiagnosticSource ETW bridge — use context metadata as label.
        string? context = ExtractStructValueField(ev, "Context");
        string? source  = ExtractStructValueField(ev, "CommandSource");
        // context is the fully-qualified DbContext name, e.g. "Acme.Data.AppDbContext" → "AppDbContext"
        string simpleName = context is { Length: > 0 }
            ? context.Split('.')[^1]
            : "";
        if (simpleName.Length > 0)
        {
            return source is { Length: > 0 }
                ? $"(EF Core: {simpleName}.{source})"
                : $"(EF Core: {simpleName})";
        }
        return "(no SQL text — EF Core via DiagnosticSource)";
    }

    /// <summary>Extracts the database name from EF Core DiagnosticSource Arguments.</summary>
    private static string GetEfDatabase(TraceEvent ev)
    {
        string? db = ExtractStructValueField(ev, "Database")
                  ?? ExtractStructValueField(ev, "database");
        if (db is { Length: > 0 } &&
            !db.Contains("Connection", StringComparison.OrdinalIgnoreCase))
            return db;
        return "(EF Core)";
    }

    // Normalize query for grouping: replace literal values with placeholders
    private static string NormalizeQuery(string sql)
    {
        if (sql.Length > 150) sql = sql[..150];
        return sql.Trim();
    }

    /// <summary>
    /// Heuristic ORM classifier based on query shape and provider signals.
    /// EF Core generates SELECT with aliases like "AS [e]" and LINQ-like patterns.
    /// NHibernate uses aliases like "this_" and "col0_". Dapper is indistinguishable from
    /// plain ADO.NET by query text alone so it falls through to AdoNet.
    /// </summary>
    private static OrmKind DetectOrm(string sql)
    {
        if (sql.StartsWith("(no SQL", StringComparison.Ordinal)) return OrmKind.Unknown;
        // EF Core: generates SELECT ... AS [e] or FROM [Table] AS [t]
        if (sql.Contains("AS [e]",  StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("AS [t]",  StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("AS [c]",  StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("AS [o]",  StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("AS [s]",  StringComparison.OrdinalIgnoreCase))
            return OrmKind.EfCore;
        // EF6: generates TOP(x) and JOIN patterns with Extent aliases
        if (sql.Contains("Extent1", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("Extent2", StringComparison.OrdinalIgnoreCase))
            return OrmKind.EfSix;
        // NHibernate: generates aliases like this_, col0_, col1_
        if (sql.Contains("this_",  StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("col0_",  StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("_0_",    StringComparison.OrdinalIgnoreCase))
            return OrmKind.NHibernate;
        // PetaPoco / RepoDb: very hard to distinguish from plain ADO, fall through
        return sql.Length > 0 ? OrmKind.AdoNet : OrmKind.Unknown;
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
