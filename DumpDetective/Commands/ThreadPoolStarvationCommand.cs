using DumpDetective.Core;
using DumpDetective.Output;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Spectre.Console;

namespace DumpDetective.Commands;

/// <summary>
/// Analyzes a .nettrace file collected with WaitHandleWait events to diagnose
/// ThreadPool starvation. Requires .NET 9+ runtime traces.
/// </summary>
internal static class ThreadPoolStarvationCommand
{
    private const string Help = """
        Usage: DumpDetective threadpool-starvation <nettrace-file> [options]

        Analyzes a .nettrace file for WaitHandleWait events (.NET 9+) to diagnose
        ThreadPool starvation caused by sync-over-async patterns.

        Collect the trace first:
          dotnet trace collect -n <ProcessName> \
              --clrevents waithandle --clreventlevel verbose \
              --duration 00:00:30

        Key signals of ThreadPool starvation:
          · Many WaitHandleWait events on ThreadPool threads
          · WaitSource=MonitorWait → Task.Result / Task.Wait() / .GetAwaiter().GetResult()
          · Stacks ending with ThreadPoolWorkQueue.Dispatch or WorkerThread.WorkerThreadStart

        Options:
          --top <N>            Top N blocking stack patterns to show (default: 10)
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help
        """;

    // WaitSource values from the .NET runtime event manifest (ClrEventSource)
    private static readonly Dictionary<int, string> WaitSourceNames = new()
    {
        [0] = "Unknown",
        [1] = "MonitorWait",
        [2] = "MonitorEnter",
        [3] = "WaitOne",
        [4] = "WaitAny",
        [5] = "WaitAll",
    };

    // Well-known CLR / EventPipe provider GUIDs — EventPipeEventSource delivers events
    // with "Provider(guid)" as the provider name when the trace has no manifest rundown.
    // This lookup resolves them to human-readable names for the diagnostic table and
    // for keyword-inference checks.
    private static readonly Dictionary<string, string> WellKnownProviderGuids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["e13c0d23-ccbc-4e12-931b-d9cc2eee27e4"] = "Microsoft-Windows-DotNETRuntime",
        ["a669021c-c450-4609-a035-5af59af4df18"] = "Microsoft-Windows-DotNETRuntimeRundown",
        ["763fd754-7086-4dfe-95eb-c01a46faf4ca"] = "Microsoft-Windows-DotNETRuntimePrivate",
        ["92f528a6-f5b8-5160-a7ee-b33da7739e29"] = "Microsoft-DotNETCore-EventPipe",
        ["8e9f5090-2d75-4e03-8a88-e4e7b5b76bd1"] = "Microsoft-DotNETCore-SampleProfiler",
    };

    // ThreadPool hill-climbing adjustment reason codes (CLR source: HillClimbing.cpp)
    private static readonly Dictionary<int, string> AdjustmentReasonNames = new()
    {
        [0] = "Warmup",
        [1] = "Initializing",
        [2] = "RandomMove",
        [3] = "ClimbingMove",
        [4] = "ChangePoint",
        [5] = "Stabilizing",
        [6] = "Starvation",         // ← key starvation signal
        [7] = "ThreadTimedOut",
        [8] = "CooperativeBlocking",
    };

    // Human-readable names for well-known CLR provider EventIDs.
    // TraceEvent shows unrecognised events as 'Task(guid)(id=N)' or 'EventID(N)';
    // this lookup decodes those for the diagnostic table.
    private static readonly Dictionary<int, string> ClrEventIdNames = new()
    {
        // GC
        [1]   = "GCStart",              [2]  = "GCEnd",
        [3]   = "GCRestartEEEnd",       [4]  = "GCHeapStats",
        [5]   = "GCCreateSegment",      [6]  = "GCFreeSegment",
        [7]   = "GCRestartEEBegin",     [8]  = "GCSuspendEEEnd",
        [9]   = "GCSuspendEEBegin",     [10] = "GCAllocationTick",
        [11]  = "GCCreateConcurrentThread", [12] = "GCTerminateConcurrentThread",
        [13]  = "GCFinalizersEnd",      [14] = "GCFinalizersBegin",
        [29]  = "GCTriggered",
        [30]  = "GCBulkMoveReferences", [31] = "GCBulkRootConditionalWeakTableElementEdge",
        [33]  = "GCBulkRootEdge",
        [35]  = "GCBulkSurvivingObjectRanges",
        [38]  = "GCBulkNode",           [39] = "GCBulkEdge",
        [143] = "GCBulkType",           [144] = "GCBulkRootEdge",
        [145] = "GCBulkRootStaticVar",  [146] = "GCBulkSurvivingObjectRanges",
        [185] = "GCBulkMovedObjectRanges", [188] = "GCBulkRootConditionalWeakTableElementEdge",
        [190] = "GCPerHeapHistory",     [191] = "GCGlobalHeapHistory",
        [192] = "GCHeapSummary",
        [203] = "GCGenerationRange",    [204] = "GCMarkStackRoots",
        [205] = "GCMarkFinalizeQueueRoots",
        // Method / JIT
        [136] = "MethodLoad",           [137] = "MethodUnload",
        [138] = "MethodUnloadVerbose",  [139] = "MethodLoadVerbose",
        [140] = "MethodDCStartVerbose", [141] = "MethodDCEndVerbose",
        // Module / Assembly / AppDomain
        [149] = "ModuleLoad",           [150] = "ModuleUnload",
        [151] = "AssemblyLoad",         [152] = "AssemblyUnload",
        [156] = "AppDomainLoad",        [154] = "AppDomainUnload",
        // ThreadPool (keyword 0x10000)
        [50]  = "IOThreadCreate",       [51] = "IOThreadTerminate",
        [52]  = "IOThreadRetire",       [53] = "IOThreadUnretire",
        [54]  = "ThreadPoolWorkerThreadStart",
        [55]  = "ThreadPoolWorkerThreadStop",
        [56]  = "ThreadPoolWorkerThreadWait",
        [57]  = "ThreadPoolWorkerThreadAdjustmentSample",
        [58]  = "ThreadPoolWorkerThreadAdjustmentAdjustment",
        [59]  = "ThreadPoolWorkerThreadAdjustmentStats",
        [60]  = "ThreadPoolWorkerThreadRetirementStart",
        [61]  = "ThreadPoolWorkerThreadRetirementStop",
        // Exception
        [80]  = "ExceptionThrown",
        // WaitHandle (.NET 9+)
        [301] = "WaitHandleWaitStart",  [302] = "WaitHandleWaitStop",
    };

    private sealed record WaitEventData(int ThreadId, int WaitSource, List<string> Frames);
    private sealed record TpThreadEvent(DateTime Ts, uint Active, uint Retired);
    private sealed record TpAdjustment(DateTime Ts, uint NewCount, int Reason, double AverageThroughput);

    private sealed record ParsedTrace(
        string TraceInfo,
        int TotalEvents,
        List<WaitEventData> WaitEvents,
        Dictionary<string, int> EventNames,
        List<TpThreadEvent> TpThreadEvents,
        List<TpAdjustment> TpAdjustments);

    private sealed record KeywordFlags(
        bool HasClrProvider,
        bool HasSampleProfiler,
        bool HasThreadPoolEvts,
        bool HasGcEvts,
        bool HasJitEvts,
        List<TpAdjustment> StarvationAdj,
        uint TpMinActive,
        uint TpMaxActive,
        uint TpFinalActive,
        bool TpWasGrowing);

    // ── Entry point ──────────────────────────────────────────────────────────

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        int top = 10;
        string? netracePath = null, outputPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--output" or "-o") && i + 1 < args.Length) outputPath = args[++i];
            else if (args[i] is "--top" && i + 1 < args.Length) int.TryParse(args[++i], out top);
            else if (!args[i].StartsWith('-') && netracePath is null) netracePath = args[i];
        }

        if (netracePath is null)
        {
            AnsiConsole.MarkupLine("[bold red]✗[/] nettrace file path required.");
            return 1;
        }
        if (!File.Exists(netracePath))
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] file not found: [dim]{Markup.Escape(netracePath)}[/]");
            return 1;
        }
        if (!netracePath.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[yellow]⚠[/] Expected a .nettrace file — proceeding anyway.");
        }

        try
        {
            using var sink = IRenderSink.Create(outputPath);
            Render(netracePath, sink, top);
            if (sink.IsFile && sink.FilePath is not null)
                AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {Markup.Escape(sink.FilePath)}");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]✗ Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    // ── Render (thin orchestrator) ────────────────────────────────────────────

    internal static void Render(string netracePath, IRenderSink sink, int top = 10)
    {
        AnsiConsole.MarkupLine(
            $"[dim]Analyzing:[/] {Markup.Escape(Path.GetFileName(netracePath))}  " +
            $"[dim]{Markup.Escape(Path.GetDirectoryName(netracePath) ?? "")}[/]");

        sink.Header(
            "Dump Detective — ThreadPool Starvation Analysis",
            $"{Path.GetFileName(netracePath)}  |  {File.GetLastWriteTime(netracePath):yyyy-MM-dd HH:mm:ss}");

        var trace         = ParseNettrace(netracePath);
        var tpEvents      = trace.WaitEvents.Where(e =>  IsThreadPoolThread(e.Frames)).ToList();
        var nonTpEvents   = trace.WaitEvents.Where(e => !IsThreadPoolThread(e.Frames)).ToList();
        int stacksPresent = trace.WaitEvents.Count(e => e.Frames.Count > 0);
        var flags         = InferKeywordFlags(trace);

        RenderSummary(sink, netracePath, trace, tpEvents.Count, nonTpEvents.Count, stacksPresent);
        RenderKeywords(sink, flags, trace.WaitEvents.Count);
        RenderThreadPoolActivity(sink, trace, flags);

        if (trace.WaitEvents.Count == 0)
        {
            RenderNoEventsSection(sink, trace, flags);
            RenderDiagnosticEventNames(sink, trace.EventNames, openByDefault: true);
            return;
        }

        RenderDiagnosticEventNames(sink, trace.EventNames, openByDefault: false);

        if (stacksPresent == 0)
            sink.Alert(AlertLevel.Warning,
                "WaitHandleWait events found but no stack traces present.",
                detail: "Stack capture requires --clreventlevel verbose AND a runtime that supports stacks for WaitHandleWait.",
                advice: "Re-collect with: dotnet trace collect -n <Process> --clrevents waithandle --clreventlevel verbose");

        RenderWaitSourceBreakdown(sink, trace.WaitEvents);
        RenderDiagnosis(sink, tpEvents);

        if (tpEvents.Count > 0 && stacksPresent > 0)
            RenderStackPatterns(sink,
                $"Top {Math.Min(top, tpEvents.Count)} Blocking Stack Patterns (ThreadPool Threads)",
                tpEvents, top, openFirst: true);

        if (nonTpEvents.Count > 0 && stacksPresent > 0)
            RenderStackPatterns(sink,
                "Top 5 Blocking Patterns (Non-ThreadPool Threads, informational)",
                nonTpEvents, 5, openFirst: false);

        sink.Reference(
            "Debug ThreadPool starvation — Microsoft Docs",
            "https://learn.microsoft.com/dotnet/core/diagnostics/debug-threadpool-starvation");
    }

    // ── Parse ─────────────────────────────────────────────────────────────────

    private static ParsedTrace ParseNettrace(string netracePath)
    {
        var waitEvents     = new List<WaitEventData>();
        var eventNames     = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tpThreadEvents = new List<TpThreadEvent>();
        var tpAdjustments  = new List<TpAdjustment>();
        string traceInfo   = "N/A";
        int    totalEvents = 0;

        CommandBase.TimedStatus("Parsing nettrace events…", _ =>
        {
            // Pass 1 — EventPipeEventSource: fast event counting + ThreadPool data.
            // Not used for WaitHandleWait stacks — evt.CallStack() always returns null
            // in EventPipe format. Stack resolution requires TraceLog (etlx). See Pass 2.
            using (var epSource = new EventPipeEventSource(netracePath))
            {
                DateTime firstTs = DateTime.MaxValue;
                DateTime lastTs  = DateTime.MinValue;

                epSource.AllEvents += evt =>
                {
                    totalEvents++;
                    if (evt.TimeStamp < firstTs) firstTs = evt.TimeStamp;
                    if (evt.TimeStamp > lastTs)  lastTs  = evt.TimeStamp;

                    string providerDisplay = ResolveProviderName(evt.ProviderName);
                    string evtName = evt.EventName;
                    if ((evtName.StartsWith("Task(",    StringComparison.Ordinal) ||
                         evtName.StartsWith("EventID(", StringComparison.Ordinal)) &&
                        providerDisplay.IndexOf("DotNETRuntime", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        ClrEventIdNames.TryGetValue((int)evt.ID, out var knownName))
                        evtName = knownName;

                    var key = $"{providerDisplay}/{evtName}(id={(int)evt.ID})";
                    eventNames.TryGetValue(key, out int nc);
                    eventNames[key] = nc + 1;

                    if (providerDisplay.IndexOf("DotNETRuntime", StringComparison.OrdinalIgnoreCase) >= 0)
                        ProcessThreadPoolEvent(evt, (int)evt.ID, tpThreadEvents, tpAdjustments);
                };

                epSource.Process();
                var duration = lastTs > firstTs ? lastTs - firstTs : TimeSpan.Zero;
                traceInfo = $"Duration: {duration:g}  |  Total events: {totalEvents:N0}";
            }

            // Pass 2 — TraceLog (etlx): WaitHandleWait events WITH resolved stacks.
            // TraceLog.CreateFromEventPipeDataFile converts nettrace → etlx, enabling
            // evt.CallStack() to return real frames — the same mechanism PerfView uses.
            // WaitHandleWaitStart (EventID 301) is not in TraceEvent's static CLR manifest,
            // so it is only visible through DynamicTraceEventParser, never ClrTraceEventParser.
            try
            {
                string etlxPath = Path.ChangeExtension(netracePath, ".etlx");
                if (!File.Exists(etlxPath))
                    TraceLog.CreateFromEventPipeDataFile(netracePath, etlxPath);

                using var traceLog = new TraceLog(etlxPath);
                var tlSource  = traceLog.Events.GetSource();
                var dynParser = new Microsoft.Diagnostics.Tracing.Parsers.DynamicTraceEventParser(tlSource);

                dynParser.All += evt =>
                {
                    if (!IsWaitHandleWaitStart(evt)) return;
                    int waitSource = ReadWaitSource(evt);
                    List<string> frames;
                    try { frames = WalkStack(evt.CallStack()); } catch { frames = []; }
                    waitEvents.Add(new WaitEventData(evt.ThreadID, waitSource, frames));
                };

                tlSource.Process();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ Stack resolution via TraceLog failed ({Markup.Escape(ex.Message)}); stacks unavailable.[/]");
                foreach (var key in eventNames.Keys
                    .Where(k => k.IndexOf("WaitHandleWaitStart", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    eventNames.TryGetValue(key, out int rawCount);
                    for (int i = 0; i < rawCount; i++)
                        waitEvents.Add(new WaitEventData(0, 0, []));
                }
            }
        });

        return new ParsedTrace(traceInfo, totalEvents, waitEvents, eventNames, tpThreadEvents, tpAdjustments);
    }

    private static void ProcessThreadPoolEvent(
        TraceEvent evt, int eid,
        List<TpThreadEvent> tpThreadEvents, List<TpAdjustment> tpAdjustments)
    {
        if (eid is 54 or 55) // ThreadPoolWorkerThreadStart / Stop
        {
            try
            {
                var active  = evt.PayloadByName("ActiveWorkerThreadCount");
                var retired = evt.PayloadByName("RetiredWorkerThreadCount");
                if (active is not null)
                    tpThreadEvents.Add(new TpThreadEvent(
                        evt.TimeStamp,
                        Convert.ToUInt32(active),
                        retired is not null ? Convert.ToUInt32(retired) : 0u));
            }
            catch { }
        }
        else if (eid == 58) // ThreadPoolWorkerThreadAdjustmentAdjustment
        {
            try
            {
                var newCount   = evt.PayloadByName("NewWorkerThreadCount");
                var reason     = evt.PayloadByName("Reason");
                var throughput = evt.PayloadByName("AverageThroughput");
                if (newCount is not null)
                    tpAdjustments.Add(new TpAdjustment(
                        evt.TimeStamp,
                        Convert.ToUInt32(newCount),
                        Convert.ToInt32(reason ?? 0),
                        throughput is not null ? Convert.ToDouble(throughput) : 0.0));
            }
            catch { }
        }
    }

    // ── Keyword inference ─────────────────────────────────────────────────────

    private static KeywordFlags InferKeywordFlags(ParsedTrace trace)
    {
        var names = trace.EventNames;

        bool hasClrProvider    = names.Keys.Any(k =>
            k.IndexOf("DotNETRuntime", StringComparison.OrdinalIgnoreCase) >= 0 ||
            k.IndexOf("e13c0d23",     StringComparison.OrdinalIgnoreCase) >= 0);
        bool hasSampleProfiler = names.Keys.Any(k =>
            k.IndexOf("SampleProfiler", StringComparison.OrdinalIgnoreCase) >= 0 ||
            k.IndexOf("8e9f5090",       StringComparison.OrdinalIgnoreCase) >= 0);
        bool hasThreadPoolEvts = trace.TpThreadEvents.Count > 0 || trace.TpAdjustments.Count > 0 ||
                                 names.Keys.Any(k => k.IndexOf("ThreadPool", StringComparison.OrdinalIgnoreCase) >= 0);
        bool hasGcEvts  = names.Keys.Any(k =>
            k.IndexOf("/GC",     StringComparison.OrdinalIgnoreCase) >= 0 ||
            k.IndexOf("/GCHeap", StringComparison.OrdinalIgnoreCase) >= 0);
        bool hasJitEvts = names.Keys.Any(k =>
            k.IndexOf("MethodLoad", StringComparison.OrdinalIgnoreCase) >= 0 ||
            k.IndexOf("/Method",    StringComparison.OrdinalIgnoreCase) >= 0);

        var  starvationAdj = trace.TpAdjustments.Where(a => a.Reason == 6).ToList();
        uint tpMin   = trace.TpThreadEvents.Count > 0 ? trace.TpThreadEvents.Min(e => e.Active) : 0;
        uint tpMax   = trace.TpThreadEvents.Count > 0 ? trace.TpThreadEvents.Max(e => e.Active) : 0;
        uint tpFinal = trace.TpThreadEvents.Count > 0 ? trace.TpThreadEvents.Last().Active       : 0;

        return new KeywordFlags(
            HasClrProvider:    hasClrProvider,
            HasSampleProfiler: hasSampleProfiler,
            HasThreadPoolEvts: hasThreadPoolEvts,
            HasGcEvts:         hasGcEvts,
            HasJitEvts:        hasJitEvts,
            StarvationAdj:     starvationAdj,
            TpMinActive:       tpMin,
            TpMaxActive:       tpMax,
            TpFinalActive:     tpFinal,
            TpWasGrowing:      trace.TpThreadEvents.Count >= 2 && tpMax > tpMin);
    }

    // ── Section renderers ─────────────────────────────────────────────────────

    private static void RenderSummary(
        IRenderSink sink, string netracePath, ParsedTrace trace,
        int tpCount, int nonTpCount, int stacksPresent)
    {
        sink.Section("Trace Summary");
        sink.KeyValues([
            ("Trace file",                    Path.GetFileName(netracePath)),
            ("Trace info",                    trace.TraceInfo),
            ("WaitHandleWait events (Start)", trace.WaitEvents.Count.ToString("N0")),
            ("  On ThreadPool threads",       tpCount.ToString("N0")),
            ("  On non-ThreadPool threads",   nonTpCount.ToString("N0")),
            ("Events with stack traces",      $"{stacksPresent:N0} / {trace.WaitEvents.Count:N0}"),
        ]);
    }

    private static void RenderKeywords(IRenderSink sink, KeywordFlags f, int waitCount)
    {
        sink.Section("Detected CLR Keywords & Providers");
        var rows = new List<string[]>();
        rows.Add(new[] { "Microsoft-Windows-DotNETRuntime",
            f.HasClrProvider    ? "✓ Present" : "✗ Absent",
            f.HasClrProvider    ? "CLR runtime events found" : "No CLR provider events — check process name" });
        rows.Add(new[] { "ThreadPool (0x10000)",
            f.HasThreadPoolEvts ? "✓ Active"  : "✗ Not seen",
            f.HasThreadPoolEvts ? "Thread-count adjustment events observed" : "No thread-count events — keyword may be inactive" });
        rows.Add(new[] { "WaitHandle (0x40000000000)",
            waitCount > 0 ? "✓ Active" : "✗ Not seen",
            waitCount > 0 ? "WaitHandleWait events present" : "WaitHandleWait keyword absent or no blocking during trace (.NET 9+ required)" });
        if (f.HasGcEvts)         rows.Add(new[] { "GC (0x1)",                     "✓ Active",  "GC events present" });
        if (f.HasJitEvts)        rows.Add(new[] { "JIT (0x10)",                    "✓ Active",  "JIT/method-load events present" });
        if (f.HasSampleProfiler) rows.Add(new[] { "CPU sampling (SampleProfiler)", "✓ Present", "Sampling provider enabled (not harmful, but not needed here)" });
        sink.Table(["Keyword / Provider", "Status", "Notes"], rows,
            "Inferred from event types present in the trace · WaitHandle keyword requires: --clrevents waithandle --clreventlevel verbose");
    }

    private static void RenderThreadPoolActivity(IRenderSink sink, ParsedTrace trace, KeywordFlags f)
    {
        if (!f.HasThreadPoolEvts) return;

        sink.Section("ThreadPool Activity");

        if (trace.TpThreadEvents.Count >= 2)
        {
            sink.KeyValues([
                ("Thread count — min (observed)",     f.TpMinActive.ToString()),
                ("Thread count — max (observed)",     f.TpMaxActive.ToString()),
                ("Thread count — final (observed)",   f.TpFinalActive.ToString()),
                ("Thread count growing during trace", f.TpWasGrowing
                    ? $"⚠ YES — grew {f.TpMinActive} → {f.TpMaxActive}" : "No (stable)"),
                ("Hill-climb Starvation adjustments", f.StarvationAdj.Count > 0
                    ? $"⚠ {f.StarvationAdj.Count} adjustment(s) with Reason=Starvation" : "None detected"),
            ]);

            if (f.TpWasGrowing && trace.WaitEvents.Count == 0)
                sink.Alert(AlertLevel.Warning,
                    $"ThreadPool thread count grew {f.TpMinActive} → {f.TpMaxActive} during the trace.",
                    detail: "A steadily rising thread count is a classic starvation indicator even without WaitHandleWait events. " +
                            "The runtime adds threads because existing ones are blocked and not returning to the pool.",
                    advice: "Re-collect while load is running to capture blocking stacks:\n" +
                            "  dotnet trace collect -n DiagnosticScenarios --clrevents waithandle --clreventlevel verbose --duration 00:00:30");
        }

        if (f.StarvationAdj.Count > 0)
        {
            sink.Alert(AlertLevel.Critical,
                $"Hill-climbing algorithm flagged Starvation in {f.StarvationAdj.Count} adjustment(s).",
                detail: "The CLR ThreadPool's hill-climbing controller labeled these adjustments as \"Starvation\". " +
                        "This is a direct built-in signal that threads were blocking and the pool couldn't keep up with demand.",
                advice: "Capture the blocking stacks:\n" +
                        "  dotnet trace collect -n DiagnosticScenarios --clrevents waithandle --clreventlevel verbose --duration 00:00:30");

            var adjRows = f.StarvationAdj
                .OrderBy(a => a.Ts)
                .Select(a => new[] { a.Ts.ToString("HH:mm:ss.fff"), a.NewCount.ToString(), GetAdjustmentReasonName(a.Reason), $"{a.AverageThroughput:F2} req/s" })
                .ToList();
            sink.Table(["Time", "New Thread Count", "Reason", "Avg Throughput"], adjRows,
                "Starvation adjustments — CLR added threads because existing ones were blocked");
        }

        if (trace.TpAdjustments.Count > 0)
        {
            var allAdjRows = trace.TpAdjustments
                .OrderBy(a => a.Ts)
                .Select(a => new[] { a.Ts.ToString("HH:mm:ss.fff"), a.NewCount.ToString(), GetAdjustmentReasonName(a.Reason), $"{a.AverageThroughput:F2} req/s" })
                .ToList();
            sink.BeginDetails($"All {trace.TpAdjustments.Count} hill-climbing adjustments", open: f.StarvationAdj.Count > 0);
            sink.Table(["Time", "New Thread Count", "Reason", "Avg Throughput"], allAdjRows,
                "Reason=Starvation(6) means the CLR scheduler identified threads were being blocked");
            sink.EndDetails();
        }
    }

    private static void RenderNoEventsSection(IRenderSink sink, ParsedTrace trace, KeywordFlags f)
    {
        if (f.HasSampleProfiler && !f.HasClrProvider)
            sink.Alert(AlertLevel.Critical,
                "Wrong trace provider — this is a CPU sampling trace, not a wait-events trace.",
                detail: "The trace was collected with Microsoft-DotNETCore-SampleProfiler which only records CPU samples. " +
                        "WaitHandleWait events require the Microsoft-Windows-DotNETRuntime CLR provider with the 'waithandle' keyword.",
                advice: "Re-collect while Bombardier (or your load tool) is running:\n" +
                        "  dotnet trace collect -n DiagnosticScenarios --clrevents waithandle --clreventlevel verbose --duration 00:00:30");
        else if (!f.HasClrProvider && trace.TotalEvents > 0)
            sink.Alert(AlertLevel.Critical,
                "No Microsoft-Windows-DotNETRuntime (CLR) events in this trace.",
                detail: $"Found {trace.TotalEvents:N0} events, but none from the CLR runtime provider. " +
                        "WaitHandleWait events are emitted by the CLR provider.",
                advice: "Re-collect with the correct provider keywords:\n" +
                        "  dotnet trace collect -n <YourProcess> --clrevents waithandle --clreventlevel verbose --duration 00:00:30");
        else if (f.HasClrProvider && f.StarvationAdj.Count > 0)
            sink.Alert(AlertLevel.Critical,
                $"Starvation detected via hill-climbing ({f.StarvationAdj.Count} adjustment(s)) but no WaitHandleWait stacks captured.",
                detail: "The CLR's own scheduler flagged this as starvation. WaitHandleWait events were either not enabled or " +
                        "the blocking happened before collection started.",
                advice: "Ensure load is running FIRST, then start the trace immediately:\n" +
                        "  dotnet trace collect -n DiagnosticScenarios --clrevents waithandle --clreventlevel verbose --duration 00:00:30");
        else if (f.HasClrProvider && f.TpWasGrowing)
            sink.Alert(AlertLevel.Warning,
                $"Thread count grew ({f.TpMinActive} → {f.TpMaxActive}) but no WaitHandleWait events captured.",
                detail: "Likely reasons:\n" +
                        "  1. 'waithandle' keyword was not included — re-collect with --clrevents waithandle.\n" +
                        "  2. Runtime is older than .NET 9 — WaitHandleWait (EventID 301) requires .NET 9+.",
                advice: "  dotnet trace collect -n DiagnosticScenarios --clrevents waithandle --clreventlevel verbose --duration 00:00:30");
        else if (f.HasClrProvider)
            sink.Alert(AlertLevel.Warning,
                "CLR events present but no WaitHandleWait events and no starvation signals detected.",
                detail: "Possible reasons:\n" +
                        "  1. App was not under load — WaitHandleWait only fires when threads block.\n" +
                        "  2. Runtime is older than .NET 9 — WaitHandleWait (EventID 301) added in .NET 9.\n" +
                        "  3. 'waithandle' keyword was not included in --clrevents.",
                advice: "Ensure Bombardier is running BEFORE starting the trace:\n" +
                        "  dotnet trace collect -n DiagnosticScenarios --clrevents waithandle --clreventlevel verbose --duration 00:00:30");
        else
            sink.Alert(AlertLevel.Warning, "No events found in this trace at all.",
                detail: "The file may be empty, corrupt, or not a valid .nettrace.",
                advice: "Re-collect with: dotnet trace collect -n <Process> --clrevents waithandle --clreventlevel verbose --duration 00:00:30");
    }

    private static void RenderWaitSourceBreakdown(IRenderSink sink, List<WaitEventData> waitEvents)
    {
        sink.Section("WaitHandleWait Events by Source");
        int total = waitEvents.Count;
        var rows = waitEvents
            .GroupBy(e => GetWaitSourceName(e.WaitSource))
            .OrderByDescending(g => g.Count())
            .Select(g => new[]
            {
                g.Key,
                g.Count().ToString("N0"),
                $"{g.Count() * 100.0 / total:F1}%",
                g.Count(e => IsThreadPoolThread(e.Frames)).ToString("N0"),
                g.Count(e => IsSyncOverAsync(e.Frames)).ToString("N0"),
            })
            .ToList();
        sink.Table(
            ["WaitSource", "Count", "% of Total", "On ThreadPool", "Sync-over-Async"], rows,
            "MonitorWait = Task.Result / Task.Wait() / .GetAwaiter().GetResult() / lock · Unknown = other primitives");
    }

    private static void RenderDiagnosis(IRenderSink sink, List<WaitEventData> tpEvents)
    {
        sink.Section("Diagnosis");

        if (tpEvents.Count == 0)
        {
            sink.Alert(AlertLevel.Info, "No blocking waits detected on ThreadPool threads.",
                detail: "All WaitHandleWait events occurred on non-ThreadPool (dedicated) threads. " +
                        "This does not indicate ThreadPool starvation.",
                advice: "If you see slow response times, check whether enough threads are created " +
                        "before load increases (dotnet-counters: dotnet.thread_pool.thread.count).");
            return;
        }

        int syncOverAsync = tpEvents.Count(IsSyncOverAsync);
        sink.Alert(
            tpEvents.Count >= 10 ? AlertLevel.Critical : AlertLevel.Warning,
            $"ThreadPool starvation likely — {tpEvents.Count} blocking wait(s) on ThreadPool threads.",
            detail: $"{syncOverAsync} of these appear to be sync-over-async patterns " +
                    "(Task.Result / Task.Wait / .GetAwaiter().GetResult()). " +
                    "Each blocked ThreadPool thread prevents it from processing queued work items, " +
                    "causing the runtime to spin up additional threads to compensate.",
            advice: "Replace sync-over-async calls with proper async/await throughout the call chain. " +
                    "Look for .Result, .Wait(), or .GetAwaiter().GetResult() in non-async methods.");
    }

    private static void RenderStackPatterns(
        IRenderSink sink, string sectionTitle,
        List<WaitEventData> events, int maxPatterns, bool openFirst)
    {
        sink.Section(sectionTitle);
        int rank = 0;
        foreach (var group in events
            .GroupBy(e => GetStackSignature(e.Frames))
            .OrderByDescending(g => g.Count())
            .Take(maxPatterns))
        {
            rank++;
            var    sample   = group.First();
            bool   isSoa    = IsSyncOverAsync(sample);
            string topFrame = GetTopAppFrame(sample.Frames);
            string title    = $"#{rank} — {group.Count()} event(s) — WaitSource: {GetWaitSourceName(sample.WaitSource)}" +
                              (isSoa           ? " [sync-over-async]" : "") +
                              (topFrame.Length > 0 ? $" — {topFrame}" : "");

            sink.BeginDetails(title, open: openFirst && rank == 1);
            if (sample.Frames.Count > 0)
                sink.Table(["#", "Frame"],
                    sample.Frames.Select((f, i) => new[] { i.ToString(), f }).ToList(),
                    $"Representative stack for {group.Count()} event(s) on thread {sample.ThreadId}");
            else
                sink.Alert(AlertLevel.Info, "Stack trace not captured for this event.",
                    advice: "Re-collect with --clreventlevel verbose");
            sink.EndDetails();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string ResolveProviderName(string rawName)
    {
        if (!rawName.StartsWith("Provider(", StringComparison.Ordinal)) return rawName;
        int start = "Provider(".Length;
        int end   = rawName.IndexOf(')', start);
        if (end <= start) return rawName;
        var guid = rawName.Substring(start, end - start);
        return WellKnownProviderGuids.TryGetValue(guid, out var resolved) ? resolved : rawName;
    }

    private static int ReadWaitSource(Microsoft.Diagnostics.Tracing.TraceEvent evt)
    {
        try
        {
            // Try by name first (works when the manifest schema is fully registered)
            var raw = evt.PayloadByName("WaitSource");

            // Fall back to positional index when PayloadByName returns null
            if (raw is null && evt.PayloadNames.Length > 0)
                raw = evt.PayloadValue(0);

            if (raw is not null)
            {
                // Handle every numeric box type DynamicTraceEventParser may use
                if (raw is byte   b)  return b;
                if (raw is int    i && i >= 0 && i <= 5) return i;  // only valid WaitSource range (0–5)
                if (raw is uint   u && u <= 5) return (int)u;
                if (raw is short  sh && sh >= 0 && sh <= 5) return sh;
                if (raw is ushort us && us <= 5) return us;
                if (raw is long   l  &&  l >= 0 &&  l <= 5) return (int)l;
                // Numeric string e.g. "1"
                if (raw is string s && int.TryParse(s, out int p) && p <= 5) return p;
                // Enum-name string e.g. "MonitorWait"
                if (raw is string nm)
                {
                    var hit = WaitSourceNames.FirstOrDefault(
                        kvp => kvp.Value.Equals(nm, StringComparison.OrdinalIgnoreCase));
                    if (hit.Value != null) return hit.Key;
                }
            }

            // Last resort: read raw payload bytes.
            // WaitHandleWait EventPipe payload: the WaitSource field is serialized as
            // a single byte (enum underlying type as emitted by the CLR EventPipe writer),
            // so reading byte 0 is correct. ReadInt32 would bleed into adjacent fields.
            if (evt.EventDataLength >= 1)
                return System.Runtime.InteropServices.Marshal.ReadByte(evt.DataStart);
        }
        catch { }
        return 0;
    }

    private static List<string> WalkStack(Microsoft.Diagnostics.Tracing.Etlx.TraceCallStack? stack)
    {
        var frames  = new List<string>();
        var current = stack;
        while (current is not null)
        {
            var addr   = current.CodeAddress;
            string mod = addr.ModuleName ?? "?";
            string mth = addr.FullMethodName ?? addr.Address.ToString("x16");
            frames.Add($"{mod}!{mth}");
            current = current.Caller;
        }
        return frames;
    }

    /// <summary>Returns true if any bottom-of-stack frame indicates a ThreadPool worker thread.</summary>
    private static bool IsThreadPoolThread(List<string> frames)
    {
        foreach (var f in frames)
        {
            if (f.Contains("ThreadPoolWorkQueue.Dispatch",                      StringComparison.OrdinalIgnoreCase) ||
                f.Contains("PortableThreadPool+WorkerThread.WorkerThreadStart", StringComparison.OrdinalIgnoreCase) ||
                f.Contains("PortableThreadPool.WorkerThread.WorkerThreadStart", StringComparison.OrdinalIgnoreCase) ||
                f.Contains("ThreadPool.WorkerThreadStart",                      StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true if the stack indicates a sync-over-async pattern
    /// (Task.Result, Task.Wait, Task.GetAwaiter().GetResult(), etc.).
    /// </summary>
    private static bool IsSyncOverAsync(WaitEventData e) => IsSyncOverAsync(e.Frames);

    private static bool IsSyncOverAsync(List<string> frames)
    {
        foreach (var f in frames)
        {
            if (f.Contains("Task.SpinThenBlockingWait",  StringComparison.OrdinalIgnoreCase) ||
                f.Contains("Task.InternalWaitCore",      StringComparison.OrdinalIgnoreCase) ||
                f.Contains("Task`1.GetResultCore",       StringComparison.OrdinalIgnoreCase) ||
                f.Contains("TaskAwaiter.GetResult",      StringComparison.OrdinalIgnoreCase) ||
                f.Contains("Task.WaitAll",               StringComparison.OrdinalIgnoreCase) ||
                f.Contains("ManualResetEventSlim.Wait",  StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Produces a compact signature for grouping duplicate stack patterns.</summary>
    private static string GetStackSignature(List<string> frames) =>
        string.Join("|", frames.Take(10));

    /// <summary>Returns the topmost application (user code) frame, skipping well-known runtime namespaces.</summary>
    private static string GetTopAppFrame(List<string> frames)
    {
        foreach (var f in frames)
        {
            if (string.IsNullOrEmpty(f) || f == "?!") continue;
            if (f.StartsWith("System.Private.",        StringComparison.OrdinalIgnoreCase)) continue;
            if (f.StartsWith("System.Runtime.",        StringComparison.OrdinalIgnoreCase)) continue;
            if (f.StartsWith("System.Threading.",      StringComparison.OrdinalIgnoreCase)) continue;
            if (f.StartsWith("System.IO.",             StringComparison.OrdinalIgnoreCase)) continue;
            if (f.Contains("Microsoft.AspNetCore.",    StringComparison.OrdinalIgnoreCase)) continue;
            if (f.Contains("System.Net.",              StringComparison.OrdinalIgnoreCase)) continue;
            return f.Length > 90 ? f[..87] + "…" : f;
        }
        return frames.FirstOrDefault() ?? "";
    }

    private static string GetWaitSourceName(int source) =>
        WaitSourceNames.TryGetValue(source, out var name) ? name : $"Unknown({source})";

    private static string GetAdjustmentReasonName(int reason) =>
        AdjustmentReasonNames.TryGetValue(reason, out var name) ? $"{name}({reason})" : $"Unknown({reason})";

    /// <summary>
    /// Returns true for WaitHandleWaitStart events (.NET 9+, CLR EventID 301).
    /// Matches by name (primary) and by EventID (fallback for traces where TraceEvent
    /// cannot decode the event name from the embedded manifest).
    /// </summary>
    private static bool IsWaitHandleWaitStart(TraceEvent evt)
    {
        // Name-based: "WaitHandleWaitStart" or any variant containing "WaitHandleWait"
        // but NOT a Stop event.
        bool nameMatch = evt.EventName.IndexOf("WaitHandleWait", StringComparison.OrdinalIgnoreCase) >= 0 &&
                         evt.EventName.IndexOf("Stop",           StringComparison.OrdinalIgnoreCase)  < 0;
        // ID-based: EventID 301 = WaitHandleWaitStart in the CLR manifest.
        // Guard: only apply ID match when the provider looks like the CLR provider
        // to avoid false positives from other providers that happen to use ID 301.
        bool idMatch = (int)evt.ID == 301 &&
                       (evt.ProviderName.IndexOf("DotNETRuntime", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        evt.ProviderName.IndexOf("System.Runtime", StringComparison.OrdinalIgnoreCase) >= 0);
        return nameMatch || idMatch;
    }

    /// <summary>
    /// Renders a collapsed diagnostic section listing every provider/event name
    /// observed in the trace, sorted by count. Invaluable when WaitHandleWait
    /// events are not found — shows exactly what the trace does contain.
    /// </summary>
    private static void RenderDiagnosticEventNames(IRenderSink sink, Dictionary<string, int> counts, bool openByDefault = false)
    {
        if (counts.Count == 0) return;

        int totalSeen = counts.Values.Sum();

        sink.Section("Trace Contents (diagnostic)");
        sink.BeginDetails($"All event types observed in this trace ({counts.Count} distinct, {totalSeen:N0} total) — expand to troubleshoot missing events", open: openByDefault);

        var rows = counts
            .OrderByDescending(x => x.Value)
            .Take(50)
            .Select(x => new[]
            {
                x.Key,
                x.Value.ToString("N0"),
                $"{x.Value * 100.0 / totalSeen:F1}%",
            })
            .ToList();

        bool hasWaitHandle   = counts.Keys.Any(k => k.IndexOf("WaitHandleWait",  StringComparison.OrdinalIgnoreCase) >= 0);
        bool hasThreadPool   = counts.Keys.Any(k => k.IndexOf("ThreadPool",       StringComparison.OrdinalIgnoreCase) >= 0);
        bool hasClrProvider  = counts.Keys.Any(k => k.IndexOf("DotNETRuntime",    StringComparison.OrdinalIgnoreCase) >= 0);

        sink.Table(["Provider/EventName", "Count", "% of Total"], rows,
            $"Top 50 of {counts.Count} distinct event types. WaitHandleWait present: {(hasWaitHandle ? "YES" : "NO")} · ThreadPool events present: {(hasThreadPool ? "YES" : "NO")} · CLR provider present: {(hasClrProvider ? "YES" : "NO")}");

        if (!hasWaitHandle)
        {
            if (!hasClrProvider)
                sink.Alert(AlertLevel.Warning,
                    "Microsoft-Windows-DotNETRuntime provider events not found in this trace.",
                    detail: "The trace may have been collected without CLR keywords, or from a non-.NET process.",
                    advice: "Re-collect: dotnet trace collect -n <YourProcess> --clrevents waithandle --clreventlevel verbose --duration 00:00:30");
            else
                sink.Alert(AlertLevel.Warning,
                    "CLR provider events are present but no WaitHandleWait events were recorded.",
                    detail: "This usually means: (1) the app was not under starvation load during collection, " +
                            "(2) the 'waithandle' keyword was not included, or (3) the runtime is older than .NET 9.",
                    advice: "Re-collect while load is running: dotnet trace collect -n <Process> --clrevents waithandle --clreventlevel verbose --duration 00:00:30");
        }

        sink.EndDetails();
    }
}

