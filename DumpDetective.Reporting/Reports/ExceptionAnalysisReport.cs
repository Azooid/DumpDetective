using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class ExceptionAnalysisReport
{
    public void Render(
        ExceptionAnalysisData data,
        IRenderSink sink,
        IReadOnlyDictionary<ulong, (int ThreadId, uint OSThreadId, string? TypeName, string? Message, int HResult, string? InnerType, IReadOnlyList<string> ThreadFrames, IReadOnlyList<string> ThrowFrames)>? activeByAddr = null,
        int top = 20,
        string? filter = null,
        bool showAddr = false,
        bool showStack = false)
    {
        activeByAddr ??= new Dictionary<ulong, (int, uint, string?, string?, int, string?, IReadOnlyList<string>, IReadOnlyList<string>)>();

        sink.Explain(
            what: "Inventories all exception objects found on the managed heap, plus any exceptions currently active (in-flight) on managed threads.",
            why: "Exception objects remain on the heap after being caught if anything holds a reference to them (logs, aggregates, retry logic).",
            impact: "'⚡ Active' exceptions are in-flight at the moment the dump was captured — they directly indicate what caused the incident.",
            bullets: ["Focus on ⚡ Active first — these are exceptions on live threads at dump time", "High count of a single type = repeated failures in a tight loop (e.g. retry storms)", "Inner exceptions often reveal the true root cause behind wrapper types", "HResult 0x80131620 = System.InvalidOperationException; 0x80131500 = System.Exception"],
            action: "Start with the ⚡ Active exception's stack trace to find the exact throw site. Cross-reference with thread-analysis to see if the thread was blocked at the time."
        );
        sink.Section("1. Exceptions on Heap");

        if (data.TotalAll == 0)
        {
            sink.Text("No exception objects found on the heap.");
            return;
        }

        var summaryRows = data.Totals
            .OrderByDescending(kv => kv.Value)
            .Take(top)
            .Select(kv =>
            {
                var samples  = data.ByType.TryGetValue(kv.Key, out var g) ? g.Samples : [];
                int actCnt   = samples.Count(s => activeByAddr.ContainsKey(s.Addr));
                // Also check by type name in case the active address isn't in the 10-sample cap
                if (actCnt == 0)
                    actCnt = activeByAddr.Values.Count(v => v.TypeName == kv.Key);
                int hr       = samples.FirstOrDefault(s => s.HResult != 0)?.HResult ?? 0;
                if (hr == 0) hr = activeByAddr.Values.FirstOrDefault(v => v.TypeName == kv.Key && v.HResult != 0).HResult;
                string inner = samples.FirstOrDefault(s => s.InnerType is not null)?.InnerType
                            ?? activeByAddr.Values.FirstOrDefault(v => v.TypeName == kv.Key && v.InnerType is not null).InnerType
                            ?? "—";
                string msg   = samples.FirstOrDefault(s => activeByAddr.ContainsKey(s.Addr) && s.Message.Length > 0)?.Message
                            ?? activeByAddr.Values.FirstOrDefault(v => v.TypeName == kv.Key && !string.IsNullOrEmpty(v.Message)).Message
                            ?? samples.FirstOrDefault(s => s.Message.Length > 0)?.Message ?? "—";
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
            }).ToList();

        sink.Table(
            ["Exception Type", "Count", "Active", "HResult", "Inner Exception", "Sample Message"],
            summaryRows,
            $"{data.TotalAll:N0} total  |  {data.Totals.Count} distinct type(s)");

        if (data.TotalAll >= 1000)
            sink.Alert(AlertLevel.Critical, $"{data.TotalAll:N0} exception objects on the heap.",
                "High exception count can indicate tight exception loops, unhandled errors, or thread exhaustion.",
                "Check active thread exceptions below and correlate with application error logs.");
        else if (data.TotalAll >= 100)
            sink.Alert(AlertLevel.Warning, $"{data.TotalAll:N0} exception objects on the heap.");

        int activeCount = activeByAddr.Count;
        if (activeCount > 0)
            sink.Alert(AlertLevel.Warning, $"{activeCount} active (in-flight) exception(s) found on managed threads.");

        RenderActiveThreadSection(sink, activeByAddr, data, showStack);
        RenderHResults(sink, data);

        if ((showStack || activeCount > 0) && data.ByType.Count > 0)
            RenderThrowStacks(sink, data, activeByAddr, top, showAddr);
        else if (showAddr && data.TotalAll > 0)
            RenderAddresses(sink, data, activeByAddr, top);

        sink.KeyValues([
            ("Exception objects on heap", data.TotalAll.ToString("N0")),
            ("Distinct types",            data.Totals.Count.ToString("N0")),
            ("Active on threads",         activeCount.ToString("N0")),
        ]);
    }

    private static void RenderActiveThreadSection(IRenderSink sink,
        IReadOnlyDictionary<ulong, (int ThreadId, uint OSThreadId, string? TypeName, string? Message, int HResult, string? InnerType, IReadOnlyList<string> ThreadFrames, IReadOnlyList<string> ThrowFrames)> activeByAddr,
        ExceptionAnalysisData data, bool showStack)
    {
        sink.Section("2. Active Thread Exceptions");
        if (activeByAddr.Count == 0) { sink.Text("No active exceptions on any managed thread."); return; }

        foreach (var (addr, (tid, ostid, clrTypeName, clrMsg, clrHresult, clrInner, threadFrames, throwFrames)) in activeByAddr)
        {
            // Primary: use info captured directly from ClrMD (always accurate)
            string? typeName = clrTypeName;
            string? message  = string.IsNullOrEmpty(clrMsg) ? null : clrMsg;
            int     hresult  = clrHresult;
            string? inner    = clrInner;

            // Fallback: search heap samples only if ClrMD didn't provide it
            if (typeName is null)
            {
                foreach (var g in data.ByType.Values)
                {
                    var s = g.Samples.FirstOrDefault(x => x.Addr == addr);
                    if (s is null) continue;
                    typeName = s.Type; message ??= s.Message; hresult = hresult != 0 ? hresult : s.HResult; inner ??= s.InnerType;
                    break;
                }
            }

            // One collapsible block per active exception, open by default
            string title = $"Thread {tid}  (OS 0x{ostid:X4})  —  {typeName ?? "?"}  ⚡ ACTIVE";
            sink.BeginDetails(title, open: true);

            var infoRows = new List<string[]>();
            if (!string.IsNullOrEmpty(message))  infoRows.Add(["Message",         message!]);
            if (hresult != 0)                    infoRows.Add(["HResult",         $"0x{hresult:X8}"]);
            if (!string.IsNullOrEmpty(inner))    infoRows.Add(["Inner Exception", inner!]);
            infoRows.Add(["Address", $"0x{addr:X16}"]);
            sink.Table(["Field", "Value"], infoRows);

            // ── Original throw stack (where throw happened) ──────────────
            // Priority: ClrException.StackTrace → heap _stackTraceString → thread stack substring
            var bestThrowFrames = throwFrames.Count > 0
                ? throwFrames
                : (data.ByType.Values.SelectMany(g => g.Samples)
                       .FirstOrDefault(s => s.Addr == addr)?.StackFrames
                   ?? (IReadOnlyList<string>)[]);

            if (bestThrowFrames.Count > 0)
                sink.Table(["Stack Frame"], bestThrowFrames.Select(f => new[] { f }).ToList(),
                    "Original throw stack — where the exception was raised");
            else
                sink.Text("⚠ Original throw stack not available (exception may have been created without being thrown, or frames were not captured).");

            // ── Live thread stack (where the thread is right now) ────────
            if (threadFrames.Count > 0)
                sink.Table(["Stack Frame"], threadFrames.Select(f => new[] { f }).ToList(),
                    "Live thread call stack — where the thread was when the dump was captured");

            sink.EndDetails();
        }
    }

    private static void RenderHResults(IRenderSink sink, ExceptionAnalysisData data)
    {
        var hresultRows = data.ByType.Values
            .SelectMany(g => g.Samples)
            .Where(s => s.HResult != 0)
            .GroupBy(s => s.HResult)
            .Select(g => new[] { $"0x{g.Key:X8}", KnownHResult(g.Key), g.Count().ToString("N0"),
                string.Join(", ", g.Select(s => s.Type.Split('.').Last()).Distinct().Take(3)) })
            .OrderByDescending(r => int.Parse(r[2].Replace(",", "")))
            .ToList();
        if (hresultRows.Count > 0)
            sink.Table(["HResult", "Meaning", "Count", "Exception Types"], hresultRows, "HResult distribution");
    }

    private static void RenderThrowStacks(IRenderSink sink, ExceptionAnalysisData data,
        IReadOnlyDictionary<ulong, (int ThreadId, uint OSThreadId, string? TypeName, string? Message, int HResult, string? InnerType, IReadOnlyList<string> ThreadFrames, IReadOnlyList<string> ThrowFrames)> activeByAddr, int top, bool showAddr)
    {
        sink.Section("3. Original Throw Stack Traces");
        foreach (var (typeName, g) in data.ByType
            .OrderByDescending(kv => kv.Value.Samples.Any(s => activeByAddr.ContainsKey(s.Addr)) ? 1 : 0)
            .ThenByDescending(kv => kv.Value.Samples.Count)
            .Take(top))
        {
            var sample = g.Samples.FirstOrDefault(s => activeByAddr.ContainsKey(s.Addr) && s.StackFrames.Count > 0)
                      ?? g.Samples.FirstOrDefault(s => s.StackFrames.Count > 0)
                      ?? g.Samples.FirstOrDefault();
            if (sample is null) continue;

            bool isActive = g.Samples.Any(s => activeByAddr.ContainsKey(s.Addr));
            int count     = data.Totals.GetValueOrDefault(typeName);
            sink.BeginDetails($"{typeName}  ({count:N0} instance(s)){(isActive ? "  ⚡ ACTIVE" : "")}", open: isActive);

            var infoRows = new List<string[]>();
            if (!string.IsNullOrEmpty(sample.Message)) infoRows.Add(["Message", sample.Message]);
            if (sample.HResult != 0) infoRows.Add(["HResult", $"0x{sample.HResult:X8}  {KnownHResult(sample.HResult)}"]);
            if (sample.InnerType is not null) infoRows.Add(["Inner Exception", sample.InnerType]);
            if (showAddr) infoRows.Add(["Address", $"0x{sample.Addr:X16}"]);
            infoRows.Add(["Status", isActive ? "⚡ ACTIVE" : "inactive"]);

            if (infoRows.Count > 0) sink.Table(["Field", "Value"], infoRows);
            if (sample.StackFrames.Count > 0)
                sink.Table(["Stack Frame"], sample.StackFrames.Select(f => new[] { f }).ToList());
            else
                sink.Text("  (no stack trace available — exception may not have been thrown yet)");
            sink.EndDetails();
        }
    }

    private static void RenderAddresses(IRenderSink sink, ExceptionAnalysisData data,
        IReadOnlyDictionary<ulong, (int ThreadId, uint OSThreadId, string? TypeName, string? Message, int HResult, string? InnerType, IReadOnlyList<string> ThreadFrames, IReadOnlyList<string> ThrowFrames)> activeByAddr, int top)
    {
        sink.Section("3. Exception Objects");
        var rows = data.ByType.Values
            .SelectMany(g => g.Samples)
            .OrderBy(s => activeByAddr.ContainsKey(s.Addr) ? 0 : 1)
            .Take(top)
            .Select(s => new[]
            {
                s.Type, $"0x{s.Addr:X16}",
                activeByAddr.TryGetValue(s.Addr, out var t) ? $"⚡ Thread {t.ThreadId}" : "inactive",
                s.HResult != 0 ? $"0x{s.HResult:X8}" : "—",
                s.Message,
            }).ToList();
        sink.Table(["Type", "Address", "Status", "HResult", "Message"], rows);
    }

    private static string KnownHResult(int hr) => hr switch
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
