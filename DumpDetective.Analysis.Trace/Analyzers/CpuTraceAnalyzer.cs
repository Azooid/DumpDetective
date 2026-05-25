using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Tracing;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using static DumpDetective.Core.Tracing.TraceEventKind;

namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses CPU sampling events from a .nettrace / .etl trace file and builds
/// a call tree + hot-path chain identical in concept to Visual Studio's CPU Usage view.
///
/// Hot path: from the call-tree root, follow the child with the highest inclusive-sample
/// count at each level.  This surfaces the single deepest chain of maximum CPU consumption.
/// </summary>
public sealed class CpuTraceAnalyzer
{
    // ── Shared event-kind cache — populated once, reused across all Consumer instances ──
    private static readonly Dictionary<string, bool> EvKind = new(StringComparer.OrdinalIgnoreCase);

    // ── Consumer — holds all per-event mutable state ─────────────────────────
    private sealed class Consumer(string? processFilter, bool filterSystem = true,
                                  bool filterUnresolved = true) : ITraceEventConsumer
    {
        private readonly string? _processFilter   = processFilter;
        private readonly bool    _filterSystem    = filterSystem;
        private readonly bool    _filterUnresolved = filterUnresolved;

        internal readonly MutableNode Root            = new("<root>", "");
        internal int                  TotalSamples;
        internal int                  UnresolvedSamples;
        internal readonly double      IntervalMs       = 1.0;
        internal readonly FrameInterner Interner       = new(initialCapacity: 1024);
        internal readonly Dictionary<int, int>    SamplesPerSecond = new();
        internal readonly HashSet<int>            ActiveThreadIds  = new();
        internal string                           TopProcessName   = "";
        internal int                              ProcessSampleCount;
        internal readonly Dictionary<string, int>  ProcessNames   = new(StringComparer.OrdinalIgnoreCase);
        internal double                           SessionDurationMs;

        public void Consume(TraceEvent ev, in TraceEventMeta meta, string processName, double timestampMs, int threadId)
        {
            if (timestampMs > SessionDurationMs)
                SessionDurationMs = timestampMs;
            if (!EvKind.TryGetValue(meta.EventName, out bool isCpuSample))
                EvKind[meta.EventName] = isCpuSample = IsCpuSampleEvent(meta.EventName);
            if (!isCpuSample) return;

            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            var callStack = ev.CallStack();
            if (callStack is null) return;

            TotalSamples++;

            int bucket = (int)(timestampMs / 1000.0);
            SamplesPerSecond.TryGetValue(bucket, out int prev);
            SamplesPerSecond[bucket] = prev + 1;
            ActiveThreadIds.Add(threadId);
            string procName = processName;
            if (procName.Length > 0)
            {
                ProcessNames.TryGetValue(procName, out int pCount);
                ProcessNames[procName] = pCount + 1;
                if (pCount + 1 > ProcessSampleCount) { ProcessSampleCount = pCount + 1; TopProcessName = procName; }
            }

            var frames = new List<(string Method, string Module)>(32);
            var cs = callStack;
            bool hasUnresolvedManaged = false;
            while (cs != null)
            {
                var addr = cs.CodeAddress;
                string rawModule = addr.ModuleName ?? "";
                string module    = Interner.Intern(rawModule);
                string method;
                if (string.IsNullOrEmpty(addr.FullMethodName))
                {
                    if (rawModule.Equals("ManagedModule", StringComparison.OrdinalIgnoreCase))
                    {
                        method = Interner.Intern("<managed, no symbols>");
                        hasUnresolvedManaged = true;
                    }
                    else if (string.IsNullOrEmpty(rawModule))
                    {
                        method = Interner.Intern("<unresolved>");
                        hasUnresolvedManaged = true;
                    }
                    else
                    {
                        method = Interner.Intern(rawModule);
                    }
                }
                else
                {
                    method = Interner.InternTruncated(addr.FullMethodName);
                }
                frames.Add((method, module));
                cs = cs.Caller;
            }
            if (hasUnresolvedManaged) UnresolvedSamples++;

            frames.Reverse();
            MutableNode cur = Root;
            for (int i = 0; i < frames.Count; i++)
            {
                var (method, module) = frames[i];
                cur = cur.GetOrAddChild(method, module);
                cur.InclusiveSamples++;
                if (i == frames.Count - 1)
                    cur.ExclusiveSamples++;
            }
        }

        public bool WantsEvent(in TraceEventMeta meta)
        {
            if (!EvKind.TryGetValue(meta.EventName, out bool v))
                EvKind[meta.EventName] = v = meta.Kind switch
                {
                    _ when meta.Kind == CpuSample => true,
                    _ when meta.IsKnown => false,
                    _ => IsCpuSampleEvent(meta.EventName)
                };
            return v;
        }

        public void OnComplete() { }

        internal bool FilterSystem    => _filterSystem;
        internal bool FilterUnresolved => _filterUnresolved;
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null, bool filterSystem = true,
                                    bool filterUnresolved = true)
        => new Consumer(processFilter, filterSystem, filterUnresolved);

    public CpuTraceData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 20,
                                     string? processFilter = null,
                                     bool filterSystem = true, bool filterUnresolved = true)
        => BuildResult(consumer, ((Consumer)consumer).SessionDurationMs, traceFileName, top, processFilter, filterSystem, filterUnresolved);

    public CpuTraceData BuildResult(ITraceEventConsumer consumer, double sessionDurationMs, string traceFileName,
                                     int top = 20, string? processFilter = null,
                                     bool filterSystem = true, bool filterUnresolved = true)
    {
        var c = (Consumer)consumer;
        int    totalSamples    = c.TotalSamples;
        double intervalMs      = c.IntervalMs;
        var    root            = c.Root;
        var    samplesPerSecond = c.SamplesPerSecond;
        var    activeThreadIds = c.ActiveThreadIds;
        string topProcessName  = c.TopProcessName;
        int    unresolvedSamples = c.UnresolvedSamples;

        if (totalSamples == 0)
        {
            return new CpuTraceData(
                $"{traceFileName}  |  0 CPU samples found — " +
                "collect with CPU sampling enabled (dotnet-trace collect --profile cpu-sampling)",
                0, intervalMs, processFilter, [], [], []);
        }

        var allNodes = new List<MutableNode>(totalSamples);
        CollectAll(root, allNodes);

        var topMethods = allNodes
            .Where(n => (n.ExclusiveSamples > 0 || n.InclusiveSamples > 0)
                     && (!filterSystem    || !IsSystemModule(n.Module))
                     && (!filterUnresolved || !IsUnresolvedPlaceholder(n.Method))
                     && n.Method != n.Module)
            .OrderByDescending(n => n.ExclusiveSamples)
            .Take(top)
            .Select(n => new CpuMethodStats(
                Method:           n.Method,
                Module:           n.Module,
                ExclusiveSamples: n.ExclusiveSamples,
                InclusiveSamples: n.InclusiveSamples,
                ExclusivePct:    totalSamples > 0 ? n.ExclusiveSamples * 100.0 / totalSamples : 0,
                InclusivePct:    totalSamples > 0 ? n.InclusiveSamples * 100.0 / totalSamples : 0))
            .ToList();

        var callTree = EffectiveRoots(root.Children, filterSystem, filterUnresolved)
            .OrderByDescending(c2 => c2.InclusiveSamples)
            .Take(top)
            .Select(n => Freeze(n, totalSamples, filterSystem: filterSystem, filterUnresolved: filterUnresolved))
            .ToList();

        var hotPath = new List<CallTreeNode>();
        var hotCur = EffectiveRoots(root.Children, filterSystem, filterUnresolved)
            .OrderByDescending(c2 => c2.InclusiveSamples)
            .FirstOrDefault();

        while (hotCur is not null)
        {
            hotPath.Add(Freeze(hotCur, totalSamples, childrenDepth: 0));
            int curSamples = hotCur.InclusiveSamples;
            hotCur = EffectiveRoots(hotCur.Children, filterSystem, filterUnresolved)
                .OrderByDescending(c2 => c2.InclusiveSamples)
                .FirstOrDefault(c2 => c2.InclusiveSamples * 100.0 / totalSamples >= 0.5
                               && c2.InclusiveSamples <= curSamples);
        }

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process filter: {processFilter}" : "") +
                      $"  |  {totalSamples:N0} CPU samples";

        double durationMs = sessionDurationMs;
        if (durationMs <= 0 && samplesPerSecond.Count > 0)
            durationMs = (samplesPerSecond.Keys.Max() - samplesPerSecond.Keys.Min() + 1) * 1000.0;
        double totalCpuMs = totalSamples * intervalMs;
        double avgCpuPct  = durationMs > 0 ? totalCpuMs / durationMs * 100.0 : 0;
        int    maxBucket  = samplesPerSecond.Count > 0 ? samplesPerSecond.Values.Max() : 0;
        double maxCpuPct  = maxBucket * intervalMs / 1000.0 * 100.0;

        var stats = new CpuStats(
            TraceDurationMs: durationMs,
            AvgCpuPct:       avgCpuPct,
            MaxCpuPct:       maxCpuPct,
            TotalCpuMs:      totalCpuMs,
            ActiveThreads:   activeThreadIds.Count,
            LogicalCores:    Environment.ProcessorCount,
            TopProcessName:  topProcessName.Length > 0 ? topProcessName : "(unknown)");

        var semanticFindings  = SemanticAnalyzer.Analyze(callTree);
        var collapsedCallTree = FrameworkCollapser.Collapse(callTree);
        var hotChains         = HotChainExtractor.Extract(collapsedCallTree);
        var categoryScores    = CategoryScorer.Score(semanticFindings);

        IReadOnlyList<double> samplesTimeline = [];
        if (samplesPerSecond.Count > 1)
        {
            int minKey   = samplesPerSecond.Keys.Min();
            int maxKey   = samplesPerSecond.Keys.Max();
            var timeline = new double[maxKey - minKey + 1];
            foreach (var kv in samplesPerSecond)
                timeline[kv.Key - minKey] = kv.Value * intervalMs / 1000.0 * 100.0;
            samplesTimeline = timeline;
        }

        return new CpuTraceData(info, totalSamples, intervalMs, processFilter,
            topMethods, hotPath, collapsedCallTree, stats,
            semanticFindings, hotChains, categoryScores, samplesTimeline,
            unresolvedSamples);
    }

    public CpuTraceData Analyze(string tracePath, int top = 20, string? processFilter = null,
                                  bool filterSystem = true, bool filterUnresolved = true)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter, filterSystem, filterUnresolved);
        }
        catch (Exception ex)
        {
            return new CpuTraceData(
                $"Failed to parse trace: {ex.Message}", 0, 0, processFilter, [], [], []);
        }
    }

    public CpuTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                 string? processFilter = null, bool filterSystem = true,
                                 bool filterUnresolved = true, Action<string>? progress = null)
    {
        try
        {
            var c = CreateConsumer(processFilter, filterSystem, filterUnresolved);
            TraceEventDispatcher.Dispatch(trace, c,
                progress);
            double durMs = Math.Max(((Consumer)c).SessionDurationMs, trace.SessionDuration.TotalMilliseconds);
            return BuildResult(c, durMs, traceFileName, top, processFilter, filterSystem, filterUnresolved);
        }
        catch (Exception ex)
        {
            return new CpuTraceData(
                $"Failed to parse trace: {ex.Message}", 0, 0, processFilter, [], [], []);
        }
    }

    private static bool IsCpuSampleEvent(string n) =>
        n.IndexOf("SampledProfile",  StringComparison.OrdinalIgnoreCase) >= 0 ||
        n.IndexOf("PerfInfo/Sample", StringComparison.OrdinalIgnoreCase) >= 0 ||
        n.IndexOf("Kernel/PerfInfo", StringComparison.OrdinalIgnoreCase) >= 0 ||
        n.IndexOf("cpu-sampling",    StringComparison.OrdinalIgnoreCase) >= 0 ||
        // .NET Core EventPipe: Microsoft-DotNETCore-SampleProfiler/Thread/Sample
        n.IndexOf("Thread/Sample",   StringComparison.OrdinalIgnoreCase) >= 0 ||
        n.IndexOf("ThreadSample",    StringComparison.OrdinalIgnoreCase) >= 0;

    private static CallTreeNode Freeze(MutableNode n, int total, int childrenDepth = 5,
                                       bool filterSystem = false, bool filterUnresolved = true)
    {
        var children = childrenDepth > 0
            ? EffectiveRoots(n.Children, filterSystem, filterUnresolved)
                .OrderByDescending(c => c.InclusiveSamples)
                .Take(20)
                .Select(c => Freeze(c, total, childrenDepth - 1, filterSystem, filterUnresolved))
                .ToList()
            : (IReadOnlyList<CallTreeNode>)[];

        return new CallTreeNode(
            Method:           n.Method,
            Module:           n.Module,
            InclusiveSamples: n.InclusiveSamples,
            ExclusiveSamples: n.ExclusiveSamples,
            InclusivePct:     total > 0 ? n.InclusiveSamples * 100.0 / total : 0,
            ExclusivePct:     total > 0 ? n.ExclusiveSamples * 100.0 / total : 0,
            Children:         children);
    }

    // A frame is "noisy" (uninformative) when any of the following apply:
    //   • filterSystem=true  and it belongs to a known OS/IIS module
    //   • filterUnresolved=true  and the method is a <unresolved> / <managed,no symbols> placeholder
    //   • method == module   → unresolved native stub (FullMethodName="" → fell back to module name)
    // Noisy frames are descended through so their children bubble up — the subtree is never dropped.
    private static bool IsNoisyFrame(MutableNode n, bool filterSystem, bool filterUnresolved = true) =>
        (filterSystem    && IsSystemModule(n.Module)) ||
        (filterUnresolved && IsUnresolvedPlaceholder(n.Method)) ||
        n.Method == n.Module;  // unresolved native stub: FullMethodName="" → fallback to module name

    private static bool IsUnresolvedPlaceholder(string method) =>
        method == "<unresolved>" || method == "<managed, no symbols>";

    private static IEnumerable<MutableNode> EffectiveRoots(
        IEnumerable<MutableNode> nodes, bool filterSystem, bool filterUnresolved = true)
    {
        foreach (var n in nodes)
        {
            if (!IsNoisyFrame(n, filterSystem, filterUnresolved))
                yield return n;
            else
                foreach (var c in EffectiveRoots(n.Children, filterSystem, filterUnresolved))
                    yield return c;
        }
    }

    private static void CollectAll(MutableNode node, List<MutableNode> result)
    {
        if (node.Method != "<root>") result.Add(node);
        foreach (var child in node.Children)
            CollectAll(child, result);
    }

    // System modules filtered by default (ntoskrnl, hal, win32k, Windows DLLs, IIS/ASP.NET native).
    // Pass --show-system to include them.
    private static bool IsSystemModule(string module) =>
        module.Equals("ntoskrnl",   StringComparison.OrdinalIgnoreCase) ||
        module.Equals("hal",        StringComparison.OrdinalIgnoreCase) ||
        module.Equals("win32k",     StringComparison.OrdinalIgnoreCase) ||
        module.Equals("win32kfull", StringComparison.OrdinalIgnoreCase) ||
        module.Equals("win32kbase", StringComparison.OrdinalIgnoreCase) ||
        module.Equals("webengine4", StringComparison.OrdinalIgnoreCase) ||
        module.Equals("iiscore",    StringComparison.OrdinalIgnoreCase) ||
        module.Equals("ntdll",      StringComparison.OrdinalIgnoreCase) ||
        module.Equals("kernelbase", StringComparison.OrdinalIgnoreCase) ||
        module.Equals("kernel32",   StringComparison.OrdinalIgnoreCase) ||
        module.Equals("user32",     StringComparison.OrdinalIgnoreCase) ||
        module.Equals("wow64",      StringComparison.OrdinalIgnoreCase) ||
        module.Equals("wow64cpu",   StringComparison.OrdinalIgnoreCase) ||
        module.StartsWith("nt!",    StringComparison.OrdinalIgnoreCase) ||
        module.StartsWith("hal!",   StringComparison.OrdinalIgnoreCase);

    // ── Mutable trie node ─────────────────────────────────────────────────────

    private sealed class MutableNode(string method, string module)
    {
        public readonly string Method = method;
        public readonly string Module = module;
        public int InclusiveSamples;
        public int ExclusiveSamples;

        private Dictionary<string, MutableNode>? _children;
        public IEnumerable<MutableNode> Children => _children is null ? [] : _children.Values;

        public MutableNode GetOrAddChild(string method, string module)
        {
            _children ??= new Dictionary<string, MutableNode>(StringComparer.Ordinal);
            if (!_children.TryGetValue(method, out var child))
                _children[method] = child = new MutableNode(method, module);
            return child;
        }
    }
}
