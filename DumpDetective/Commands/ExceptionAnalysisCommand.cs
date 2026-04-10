using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DumpDetective.Commands;

internal static class ExceptionAnalysisCommand
{
    private const string Help = """
        Usage: DumpDetective exception-analysis <dump-file> [options]

        Options:
          -n, --top <N>      Top N exception types to show (default: 20)
          -f, --filter <t>   Only types whose name contains <t>
          -a, --addresses    Include object addresses in the detail table
          -s, --stack        Show original throw stack trace per exception type
          -o, --output <f>   Write report to file (.md / .html / .txt)
          -h, --help         Show this help
        """;

    // Per-instance detail; collected for up to MaxPerType instances per type.
    private sealed record ExceptionRecord(
        ulong        Addr,
        string       Type,
        string       Message,
        int          HResult,
        string?      InnerType,
        bool         IsActive,
        int?         ThreadId,
        uint         OSThreadId,
        List<string> StackTrace);

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        int top = 20; string? filter = null; bool showAddr = false; bool showStack = false;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--top" or "-n") && i + 1 < args.Length)         int.TryParse(args[++i], out top);
            else if ((args[i] is "--filter" or "-f") && i + 1 < args.Length) filter = args[++i];
            else if (args[i] is "--addresses" or "-a")                        showAddr = true;
            else if (args[i] is "--stack" or "-s")                            showStack = true;
        }
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, top, filter, showAddr, showStack));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, int top = 20, string? filter = null,
                                bool showAddr = false, bool showStack = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);
        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        // Build active-exception lookup from thread data before the heap walk (O(1) per object)
        var activeByAddr = new Dictionary<ulong, (int ThreadId, uint OSThreadId)>();
        foreach (var t in ctx.Runtime.Threads)
        {
            if (t.CurrentException is not null)
                activeByAddr[t.CurrentException.Address] = (t.ManagedThreadId, t.OSThreadId);
        }

        // Heap walk — collect per-instance detail capped at MaxPerType per type
        const int MaxPerType = 10;
        var byType   = new Dictionary<string, List<ExceptionRecord>>();
        var totals   = new Dictionary<string, int>();
        int totalAll = 0;

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("blue"))
            .Start("Scanning exception objects...", ctx2 =>
            {
                var watch   = Stopwatch.StartNew();
                long visited = 0;

                foreach (var obj in ctx.Heap.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                    if (!DumpHelpers.IsExceptionType(obj.Type)) continue;

                    var typeName = obj.Type.Name ?? "<unknown>";
                    if (filter is not null && !typeName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    totalAll++;
                    ref int tCount = ref CollectionsMarshal.GetValueRefOrAddDefault(totals, typeName, out _);
                    tCount++;

                    if (!byType.TryGetValue(typeName, out var list))
                    {
                        list = new List<ExceptionRecord>(capacity: MaxPerType);
                        byType[typeName] = list;
                    }

                    bool isActive = activeByAddr.ContainsKey(obj.Address);
                    // Always capture active exceptions; cap inactive at MaxPerType
                    if (list.Count < MaxPerType || isActive)
                    {
                        activeByAddr.TryGetValue(obj.Address, out var tInfo);
                        list.Add(ExtractRecord(obj, typeName, isActive, tInfo));
                    }

                    visited++;
                    if (watch.Elapsed.TotalSeconds >= 1)
                    {
                        ctx2.Status($"Scanning exception objects — {visited:N0} visited, {totalAll:N0} found");
                        watch.Restart();
                    }
                }
            });

        sink.Header("Exception Analysis",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {totalAll:N0} exception object(s)  |  {activeByAddr.Count} active");

        // ── 1. Heap summary by type ───────────────────────────────────────────
        sink.Section("1. Exceptions on Heap");
        var summaryRows = totals
            .OrderByDescending(kv => kv.Value)
            .Take(top)
            .Select(kv =>
            {
                var recs     = byType.TryGetValue(kv.Key, out var l) ? l : [];
                int actCnt   = recs.Count(r => r.IsActive);
                int hr       = recs.FirstOrDefault(r => r.HResult != 0)?.HResult ?? 0;
                string inner = recs.FirstOrDefault(r => r.InnerType is not null)?.InnerType ?? "—";
                // Sample message: prefer active instance, then any with a non-empty message
                string msg   = recs.FirstOrDefault(r => r.IsActive && r.Message.Length > 0)?.Message
                            ?? recs.FirstOrDefault(r => r.Message.Length > 0)?.Message ?? "—";
                if (msg.Length > 80) msg = msg[..77] + "…";
                return new[]
                {
                    kv.Key,
                    kv.Value.ToString("N0"),
                    actCnt > 0 ? $"⚡ {actCnt}" : "—",
                    hr != 0 ? $"0x{hr:X8}" : "—",
                    inner,
                    msg,
                };
            })
            .ToList();
        sink.Table(
            ["Exception Type", "Count", "Active", "HResult", "Inner Exception", "Sample Message"],
            summaryRows,
            $"{totalAll:N0} total  |  {totals.Count} distinct type(s)");

        // ── 2. Active thread exceptions ───────────────────────────────────────
        sink.Section("2. Active Thread Exceptions");
        var activeThreads = ctx.Runtime.Threads.Where(t => t.CurrentException is not null).ToList();
        if (activeThreads.Count == 0)
        {
            sink.Text("No active exceptions on any managed thread.");
        }
        else
        {
            var threadRows = activeThreads.Select(t =>
            {
                var ex = t.CurrentException!;
                return new[]
                {
                    t.ManagedThreadId.ToString(),
                    $"0x{t.OSThreadId:X4}",
                    ex.Type?.Name ?? "?",
                    ex.Message ?? "—",
                    ex.HResult != 0 ? $"0x{ex.HResult:X8}" : "—",
                    ex.Inner?.Type?.Name ?? "—",
                };
            }).ToList();
            sink.Table(
                ["Managed ID", "OS Thread", "Exception Type", "Message", "HResult", "Inner Exception"],
                threadRows);

            if (showStack)
            {
                foreach (var t in activeThreads)
                {
                    var ex = t.CurrentException!;
                    sink.BeginDetails(
                        $"Thread {t.ManagedThreadId}  OS:0x{t.OSThreadId:X4}  ⚡ {ex.Type?.Name ?? "?"}  — current call stack",
                        open: true);
                    var frameRows = ex.StackTrace
                        .Take(20)
                        .Select(f => new[] { f.Method?.Signature ?? f.ToString() ?? "?" })
                        .ToList();
                    if (frameRows.Count > 0)
                        sink.Table(["Stack Frame"], frameRows);
                    else
                        sink.Text("  (no managed frames)");
                    sink.EndDetails();
                }
            }
        }
        // ── 2b. HResult grouping ───────────────────────────────────────────────────
        var hresultGroups = byType.Values
            .SelectMany(l => l)
            .Where(r => r.HResult != 0)
            .GroupBy(r => r.HResult)
            .Select(g =>
            {
                string label = KnownHResult(g.Key);
                string types = string.Join(", ", g.Select(r => r.Type.Split('.').Last()).Distinct().Take(3));
                return new[] { $"0x{g.Key:X8}", label, g.Count().ToString("N0"), types };
            })
            .OrderByDescending(r => int.Parse(r[2].Replace(",", "")))
            .ToList();
        if (hresultGroups.Count > 0)
            sink.Table(["HResult", "Meaning", "Count", "Exception Types"], hresultGroups,
                "HResult distribution across all exception objects");
        // ── 3. Original throw stacks (--stack) ───────────────────────────────
        if (showStack && byType.Count > 0)
        {
            sink.Section("3. Original Throw Stack Traces");
            foreach (var (typeName, recs) in byType
                .OrderByDescending(kv => kv.Value.Any(r => r.IsActive) ? 1 : 0)
                .ThenByDescending(kv => kv.Value.Count)
                .Take(top))
            {
                // Prefer active instance with a stack; fall back to any with a stack
                var sample = recs.FirstOrDefault(r => r.IsActive && r.StackTrace.Count > 0)
                          ?? recs.FirstOrDefault(r => r.StackTrace.Count > 0)
                          ?? recs.FirstOrDefault();
                if (sample is null) continue;

                bool isActive = recs.Any(r => r.IsActive);
                int  count    = totals.GetValueOrDefault(typeName);
                string activeTag = isActive ? "  ⚡ ACTIVE" : "";
                string detailTitle =
                    $"{typeName}  ({count:N0} instance(s)){activeTag}";

                sink.BeginDetails(detailTitle, open: isActive);

                var infoRows = new List<string[]>();
                if (!string.IsNullOrEmpty(sample.Message))
                    infoRows.Add(["Message", sample.Message]);
                if (sample.HResult != 0)
                    infoRows.Add(["HResult", $"0x{sample.HResult:X8}  {KnownHResult(sample.HResult)}".TrimEnd()]);
                if (sample.InnerType is not null)
                    infoRows.Add(["Inner Exception", sample.InnerType]);
                if (showAddr)
                    infoRows.Add(["Address", $"0x{sample.Addr:X16}"]);
                string status = sample.IsActive ? $"⚡ ACTIVE on thread {sample.ThreadId}" : "inactive";
                infoRows.Add(["Status", status]);

                if (infoRows.Count > 0)
                    sink.Table(["Field", "Value"], infoRows);

                if (sample.StackTrace.Count > 0)
                    sink.Table(["Original Throw Stack Frame"],
                        sample.StackTrace.Select(f => new[] { f }).ToList());
                else
                    sink.Text("  (no stack trace available — exception may not have been thrown yet)");

                sink.EndDetails();
            }
        }

        // ── 4. Address detail table (--addresses without --stack) ─────────────
        if (showAddr && !showStack && totalAll > 0)
        {
            sink.Section("3. Exception Objects");
            var addrRows = byType.Values
                .SelectMany(l => l)
                .OrderBy(r => r.IsActive ? 0 : 1)
                .Take(top)
                .Select(r => new[]
                {
                    r.Type,
                    $"0x{r.Addr:X16}",
                    r.IsActive ? $"⚡ Thread {r.ThreadId}" : "inactive",
                    r.HResult != 0 ? $"0x{r.HResult:X8}" : "—",
                    r.Message,
                })
                .ToList();
            sink.Table(["Type", "Address", "Status", "HResult", "Message"], addrRows);
        }

        sink.KeyValues([
            ("Exception objects on heap", totalAll.ToString("N0")),
            ("Distinct types",            totals.Count.ToString("N0")),
            ("Active on threads",         activeByAddr.Count.ToString("N0")),
        ]);
    }

    private static ExceptionRecord ExtractRecord(
        ClrObject obj, string typeName, bool isActive,
        (int ThreadId, uint OSThreadId) threadInfo)
    {
        string       msg     = "";
        int          hresult = 0;
        string?      inner   = null;
        var          stack   = new List<string>();

        try
        {
            var f = obj.Type?.GetFieldByName("_message");
            if (f is not null)
            {
                var o = f.ReadObject(obj, interior: false);
                if (o.IsValid) msg = o.AsString(maxLength: 120) ?? "";
            }
        }
        catch { }

        try
        {
            var f = obj.Type?.GetFieldByName("_HResult");
            if (f is not null)
                hresult = f.Read<int>(obj, interior: false);
        }
        catch { }

        try
        {
            var f = obj.Type?.GetFieldByName("_innerException");
            if (f is not null)
            {
                var o = f.ReadObject(obj, interior: false);
                if (o.IsValid && o.Type is not null)
                    inner = o.Type.Name;
            }
        }
        catch { }

        try
        {
            // _stackTraceString holds the formatted stack after the exception is thrown/caught
            var f = obj.Type?.GetFieldByName("_stackTraceString");
            if (f is not null)
            {
                var o = f.ReadObject(obj, interior: false);
                if (o.IsValid)
                {
                    var raw = o.AsString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        stack.AddRange(
                            raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                               .Select(l => l.Trim())
                               .Where(l => l.Length > 0));
                    }
                }
            }
        }
        catch { }

        return new ExceptionRecord(obj.Address, typeName, msg, hresult, inner,
                                   isActive, isActive ? threadInfo.ThreadId : null,
                                   isActive ? threadInfo.OSThreadId : 0, stack);
    }

    static string KnownHResult(int hr) => hr switch
    {
        unchecked((int)0x80004005) => "E_FAIL (general failure)",
        unchecked((int)0x80070005) => "E_ACCESSDENIED",
        unchecked((int)0x80070057) => "E_INVALIDARG",
        unchecked((int)0x8007000E) => "E_OUTOFMEMORY",
        unchecked((int)0x80131500) => "COR_E_EXCEPTION",
        unchecked((int)0x80131501) => "COR_E_ARITHMETIC",
        unchecked((int)0x80131502) => "COR_E_ARRAYTYPEMISMATCH",
        unchecked((int)0x80131503) => "COR_E_BADIMAGEFORMAT",
        unchecked((int)0x80131509) => "COR_E_INVALIDOPERATION",
        unchecked((int)0x8013150A) => "COR_E_IO",
        unchecked((int)0x80131517) => "COR_E_NULLREFERENCE",
        unchecked((int)0x8013152D) => "COR_E_STACKOVERFLOW",
        unchecked((int)0x80131535) => "COR_E_TIMEOUT",
        unchecked((int)0x80131539) => "COR_E_UNAUTHORIZEDACCESS",
        unchecked((int)0x80131620) => "COR_E_THREADABORT",
        unchecked((int)0x80070006) => "E_HANDLE",
        unchecked((int)0x8007001F) => "ERROR_GEN_FAILURE",
        unchecked((int)0x800700B7) => "ERROR_ALREADY_EXISTS",
        unchecked((int)0x8007045A) => "ERROR_GRACEFUL_DISCONNECT",
        unchecked((int)0x80072EE7) => "WININET_E_NAME_NOT_RESOLVED",
        unchecked((int)0x80072EFE) => "WININET_E_CONNECTION_ABORTED",
        unchecked((int)0x80072EFF) => "WININET_E_CONNECTION_RESET",
        _ => "",
    };
}
