using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Tracing;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Tracing.Etlx;

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
        // Mutable trie node used during collection
        var root = new MutableNode("<root>", "");
        int totalSamples = 0;
        double intervalMs = 1.0; // default — overridden if we can read it from the trace

        // Frame string interner — deduplicates method/module name strings across millions of samples
        var interner = new FrameInterner(initialCapacity: 1024);

        // Per-second sample buckets for max CPU calculation (key = floor(ms/1000))
        var samplesPerSecond = new Dictionary<int, int>();
        var activeThreadIds  = new HashSet<int>();
        string topProcessName = "";
        int processSampleCount = 0;
        var processNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Counts samples where ≥1 frame could not be resolved to a method name.
        // Covers: (a) ManagedModule frames (CLR rundown absent) and
        //         (b) completely unresolved frames (no module, no method — PerfView shows as <<? !?>>) .
        int unresolvedSamples = 0;

        long total = trace.EventCount;
        long processed = 0;
        long lastProgressMs = 0;
        var evKind = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var ev in trace.Events)
            {
                // CPU sample events appear as "PerfInfo/Sample" (kernel ETW) or
                // "Microsoft-Windows-DotNETRuntime/SampledProfile" (CLR ETW) or
                // similar names in EventPipe .nettrace.
                processed++;
                if (progress is not null && Environment.TickCount64 - lastProgressMs >= 200)
                {
                    progress($"{totalSamples:N0} CPU samples");
                    lastProgressMs = Environment.TickCount64;
                }
                string evName = ev.EventName ?? "";
                if (!evKind.TryGetValue(evName, out bool isCpuSample))
                    evKind[evName] = isCpuSample = IsCpuSampleEvent(evName);
                if (!isCpuSample) continue;

                // Optional process filter
                if (processFilter is not null &&
                    !(ev.ProcessName ?? "").Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var callStack = ev.CallStack();
                if (callStack is null) continue;

                totalSamples++;

                // Track per-second buckets and active threads for CPU stats
                int bucket = (int)(ev.TimeStampRelativeMSec / 1000.0);
                samplesPerSecond.TryGetValue(bucket, out int prev);
                samplesPerSecond[bucket] = prev + 1;
                activeThreadIds.Add(ev.ThreadID);
                string procName = ev.ProcessName ?? "";
                if (procName.Length > 0)
                {
                    processNames.TryGetValue(procName, out int pCount);
                    processNames[procName] = pCount + 1;
                    if (pCount + 1 > processSampleCount) { processSampleCount = pCount + 1; topProcessName = procName; }
                }

                // Collect frames bottom-up (call stack is stored root→leaf in TraceLog
                // as Caller chain, leaf at the top).
                var frames = new List<(string Method, string Module)>(32);
                var cs = callStack;
                bool hasUnresolvedManaged = false;
                while (cs != null)
                {
                    var addr = cs.CodeAddress;
                    string rawModule = addr.ModuleName ?? "";
                    string module    = interner.Intern(rawModule);
                    string method;
                    if (string.IsNullOrEmpty(addr.FullMethodName))
                    {
                        // "ManagedModule" is TraceLog's synthetic module name for managed JIT code
                        // that couldn't be resolved (CLR rundown events not present in the trace).
                        // Using a distinct placeholder keeps these frames visible in the call tree
                        // instead of silently dropping them via the method==module noisy-frame filter.
                        if (rawModule.Equals("ManagedModule", StringComparison.OrdinalIgnoreCase))
                        {
                            // Managed JIT code — CLR rundown events were not captured.
                            // Angle brackets avoid the [Assembly.Qualifier] stripping in CleanIlMethod.
                            method = interner.Intern("<managed, no symbols>");
                            hasUnresolvedManaged = true;
                        }
                        else if (string.IsNullOrEmpty(rawModule))
                        {
                            // Completely unresolved: no module info at all.
                            // PerfView shows these as <<? !?>>. They represent real CPU time
                            // (often the bulk of samples in traces without symbol resolution).
                            // Angle brackets avoid the [Assembly.Qualifier] stripping in CleanIlMethod.
                            method = interner.Intern("<unresolved>");
                            hasUnresolvedManaged = true;
                        }
                        else
                        {
                            method = interner.Intern(rawModule);  // native stub: method=module → noisy (descended through)
                        }
                    }
                    else
                    {
                        method = interner.InternTruncated(addr.FullMethodName);
                    }
                    frames.Add((method, module));
                    cs = cs.Caller;
                }
                if (hasUnresolvedManaged) unresolvedSamples++;

                // frames[0] = leaf (executing), frames[^1] = root caller
                // Reverse so we insert root→leaf into the trie
                frames.Reverse();
                MutableNode cur = root;
                for (int i = 0; i < frames.Count; i++)
                {
                    var (method, module) = frames[i];
                    cur = cur.GetOrAddChild(method, module);
                    cur.InclusiveSamples++;
                    if (i == frames.Count - 1)
                        cur.ExclusiveSamples++;  // leaf = actually executing
                }
            }
        }
        catch (Exception ex)
        {
            return new CpuTraceData(
                $"Failed to parse trace: {ex.Message}", 0, 0, processFilter, [], [], []);
        }

        if (totalSamples == 0)
        {
            return new CpuTraceData(
                $"{traceFileName}  |  0 CPU samples found — " +
                "collect with CPU sampling enabled (dotnet-trace collect --profile cpu-sampling)",
                0, intervalMs, processFilter, [], [], []);
        }

        // ── Build flat top-methods table ──────────────────────────────────────
        var allNodes = new List<MutableNode>(totalSamples);
        CollectAll(root, allNodes);

        var topMethods = allNodes
            .Where(n => (n.ExclusiveSamples > 0 || n.InclusiveSamples > 0)
                     && (!filterSystem    || !IsSystemModule(n.Module))
                     && (!filterUnresolved || !IsUnresolvedPlaceholder(n.Method))
                     && n.Method != n.Module)  // skip unresolved native stubs (method == module fallback)
            .OrderByDescending(n => n.ExclusiveSamples)
            .Take(top)
            .Select(n => new CpuMethodStats(
                Method:          n.Method,
                Module:          n.Module,
                ExclusiveSamples: n.ExclusiveSamples,
                InclusiveSamples: n.InclusiveSamples,
                ExclusivePct:    totalSamples > 0 ? n.ExclusiveSamples * 100.0 / totalSamples : 0,
                InclusivePct:    totalSamples > 0 ? n.InclusiveSamples * 100.0 / totalSamples : 0))
            .ToList();

        // ── Build immutable call tree (top-level children of root) ────────────
        var callTree = EffectiveRoots(root.Children, filterSystem, filterUnresolved)
            .OrderByDescending(c => c.InclusiveSamples)
            .Take(top)
            .Select(n => Freeze(n, totalSamples, filterSystem: filterSystem, filterUnresolved: filterUnresolved))
            .ToList();

        // ── Hot path: follow highest-inclusive child at each level ────────────
        // EffectiveRoots descends through system frames so the path starts at the
        // first user/managed frame and never stalls inside kernel stubs.
        //
        // Cycle detection: in a merged call tree built from many concurrent requests,
        // the same entry-point frames (e.g. IIS pipeline) accumulate enormous inclusive
        // counts.  When the path reaches a deep leaf (e.g. Task.Run) the "highest child"
        // might be that same IIS entry frame with far MORE inclusive samples than the
        // current position.  A genuine descent always has non-increasing inclusive counts,
        // so we stop as soon as the best candidate has MORE samples than the current frame.
        // This correctly allows legitimate repeats (e.g. WrapEntityServiceAction called
        // multiple times for nested APM spans) while stopping the pipeline re-entry loop.
        var hotPath = new List<CallTreeNode>();
        var hotCur = EffectiveRoots(root.Children, filterSystem, filterUnresolved)
            .OrderByDescending(c => c.InclusiveSamples)
            .FirstOrDefault();

        while (hotCur is not null)
        {
            hotPath.Add(Freeze(hotCur, totalSamples, childrenDepth: 0));
            int curSamples = hotCur.InclusiveSamples;
            hotCur = EffectiveRoots(hotCur.Children, filterSystem, filterUnresolved)
                .OrderByDescending(c => c.InclusiveSamples)
                .FirstOrDefault(c => c.InclusiveSamples * 100.0 / totalSamples >= 0.5
                               && c.InclusiveSamples <= curSamples);  // must not go UP — that's a cycle
        }

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process filter: {processFilter}" : "") +
                      $"  |  {totalSamples:N0} CPU samples";

        // ── Compute CPU utilisation stats ─────────────────────────────────────
        // TraceLog.SessionDuration gives the wall-clock span of the trace.
        // Each sample represents ~intervalMs of CPU time on one thread.
        // Avg CPU % = (samples × intervalMs) / traceDurationMs × 100
        // Max CPU % = max samples in any 1-second bucket × intervalMs / 1000 × 100
        double durationMs   = trace.SessionDuration.TotalMilliseconds;
        if (durationMs <= 0 && samplesPerSecond.Count > 0)
        {
            // Fallback: derive duration from first/last sample bucket
            durationMs = (samplesPerSecond.Keys.Max() - samplesPerSecond.Keys.Min() + 1) * 1000.0;
        }
        double totalCpuMs   = totalSamples * intervalMs;
        double avgCpuPct    = durationMs > 0 ? totalCpuMs / durationMs * 100.0 : 0;
        int    maxBucket    = samplesPerSecond.Count > 0 ? samplesPerSecond.Values.Max() : 0;
        double maxCpuPct    = maxBucket * intervalMs / 1000.0 * 100.0;
        int    logicalCores = Environment.ProcessorCount;  // host machine cores (best available)

        var stats = new CpuStats(
            TraceDurationMs:  durationMs,
            AvgCpuPct:        avgCpuPct,
            MaxCpuPct:        maxCpuPct,
            TotalCpuMs:       totalCpuMs,
            ActiveThreads:    activeThreadIds.Count,
            LogicalCores:     logicalCores,
            TopProcessName:   topProcessName.Length > 0 ? topProcessName : "(unknown)");

        // ── Semantic analysis pipeline ─────────────────────────────────────────
        // Run SemanticAnalyzer on the ORIGINAL call tree so detectors see real frame
        // names (e.g. "Shaper`1+SimpleEnumerator.MoveNext") not collapsed labels.
        // Then collapse for display purposes only.
        var semanticFindings = SemanticAnalyzer.Analyze(callTree);
        var collapsedCallTree = FrameworkCollapser.Collapse(callTree);

        var hotChains        = HotChainExtractor.Extract(collapsedCallTree);
        var categoryScores   = CategoryScorer.Score(semanticFindings);

        // ── CPU timeline sparkline ─────────────────────────────────────────────
        // Convert samplesPerSecond dict → sorted list of sample counts per second bucket.
        // Scale to CPU% (samples × intervalMs / 1000 × 100) so sparkline is in % terms.
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

    private static bool IsCpuSampleEvent(string n) =>
        n.IndexOf("SampledProfile",  StringComparison.OrdinalIgnoreCase) >= 0 ||
        n.IndexOf("PerfInfo/Sample", StringComparison.OrdinalIgnoreCase) >= 0 ||
        n.IndexOf("Kernel/PerfInfo", StringComparison.OrdinalIgnoreCase) >= 0 ||
        n.IndexOf("cpu-sampling",    StringComparison.OrdinalIgnoreCase) >= 0;

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
