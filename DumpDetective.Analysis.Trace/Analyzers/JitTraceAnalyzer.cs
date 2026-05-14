using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;


namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses JIT compilation events (Method/JittingStarted, Method/LoadVerbose, MethodJitInliningSucceeded)
/// from a .nettrace / .etl trace to show which methods cost the most to compile at startup or under load.
/// </summary>
public sealed class JitTraceAnalyzer
{
    public JitTraceData Analyze(string tracePath, int top = 30, string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new JitTraceData($"Failed: {ex.Message}", processFilter, 0, 0, 0, 0, [], [], [], false);
        }
    }

    private static readonly Dictionary<string, byte> EvKind = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly Dictionary<long, (double startMs, string method, string module, int ilSize)> InFlight = new();
        internal readonly Dictionary<string, MethodAcc> ByMethod = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, ModuleAcc> ByModule = new(StringComparer.OrdinalIgnoreCase);
        internal bool TimingAvailable;

        public void Consume(TraceEvent ev, string evName, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            if (!EvKind.TryGetValue(evName, out byte kind))
                EvKind[evName] = kind =
                    evName.IndexOf("JittingStarted",   StringComparison.OrdinalIgnoreCase) >= 0 ||
                    evName.IndexOf("MethodJitStart",   StringComparison.OrdinalIgnoreCase) >= 0 ? (byte)1 :
                    evName.IndexOf("MethodLoadVerbose",StringComparison.OrdinalIgnoreCase) >= 0 ||
                    evName.IndexOf("Method/LoadVerbose",StringComparison.OrdinalIgnoreCase) >= 0 ||
                    evName.IndexOf("MethodLoad/Verbose",StringComparison.OrdinalIgnoreCase) >= 0 ? (byte)2 :
                    evName.IndexOf("MethodLoad",       StringComparison.OrdinalIgnoreCase) >= 0 &&
                    evName.IndexOf("Verbose",          StringComparison.OrdinalIgnoreCase) < 0 ? (byte)3 :
                    (byte)0;
            if (kind == 0) return;

            if (kind == 1)
            {
                long   key    = BuildKey(ev);
                string method = FullName(ev);
                string module = ModuleName(ev);
                int    ilSize = SafeInt(ev, "MethodILSize");
                InFlight[key] = (timestampMs, method, module, ilSize);
                return;
            }

            if (kind == 2)
            {
                long key = BuildKey(ev);
                int  nativeSize = SafeInt(ev, "MethodSize");
                string method = FullName(ev);
                string module = ModuleName(ev);

                double jitMs = 0;
                int    ilSize = 0;
                if (InFlight.TryGetValue(key, out var start))
                {
                    jitMs  = timestampMs - start.startMs;
                    ilSize = start.ilSize;
                    if (start.method.Length > 0) method = start.method;
                    if (start.module.Length > 0) module = start.module;
                    InFlight.Remove(key);
                    TimingAvailable = true;
                }

                if (method.Length == 0) method = "(unknown)";

                if (!ByMethod.TryGetValue(method, out var macc))
                    ByMethod[method] = macc = new MethodAcc(module, ilSize, nativeSize);
                macc.Count++;
                macc.TotalJitMs += jitMs;
                if (jitMs > macc.MaxJitMs) macc.MaxJitMs = jitMs;

                if (!ByModule.TryGetValue(module, out var modacc))
                    ByModule[module] = modacc = new ModuleAcc();
                modacc.MethodCount++;
                modacc.TotalJitMs += jitMs;
                return;
            }

            // kind == 3: MethodLoad (non-verbose)
            {
                string method = FullName(ev);
                string module = ModuleName(ev);
                if (method.Length == 0) return;
                if (!ByMethod.TryGetValue(method, out var macc))
                    ByMethod[method] = macc = new MethodAcc(module, 0, 0);
                macc.Count++;

                if (!ByModule.TryGetValue(module, out var modacc))
                    ByModule[module] = modacc = new ModuleAcc();
                modacc.MethodCount++;
            }
        }

        public bool WantsEvent(string eventName) { if (!EvKind.TryGetValue(eventName, out byte v)) { v = eventName.IndexOf("JittingStarted", StringComparison.OrdinalIgnoreCase) >= 0 || eventName.IndexOf("MethodJitStart", StringComparison.OrdinalIgnoreCase) >= 0 ? (byte)1 : eventName.IndexOf("MethodLoadVerbose", StringComparison.OrdinalIgnoreCase) >= 0 || eventName.IndexOf("Method/LoadVerbose", StringComparison.OrdinalIgnoreCase) >= 0 ? (byte)2 : eventName.IndexOf("MethodLoad", StringComparison.OrdinalIgnoreCase) >= 0 && eventName.IndexOf("Verbose", StringComparison.OrdinalIgnoreCase) < 0 ? (byte)3 : (byte)0; EvKind[eventName] = v; } return v != 0; }

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null)
        => new Consumer(processFilter);

    public JitTraceData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 30,
                                     string? processFilter = null)
    {
        var c = (Consumer)consumer;
        if (c.ByMethod.Count == 0)
        {
            return new JitTraceData(
                $"{traceFileName}  |  0 JIT events — collect with --providers Microsoft-Windows-DotNETRuntime:0x10:5",
                processFilter, 0, 0, 0, 0, [], [], [], false);
        }

        double totalJitMs = c.ByMethod.Values.Sum(a => a.TotalJitMs);
        double maxJitMs   = c.ByMethod.Values.Max(a => a.MaxJitMs);
        int    total      = c.ByMethod.Values.Sum(a => a.Count);

        var topByTime = c.ByMethod
            .Where(kv => kv.Value.TotalJitMs > 0)
            .OrderByDescending(kv => kv.Value.TotalJitMs)
            .Take(top)
            .Select(kv => new JitMethodEntry(kv.Key, kv.Value.Module, kv.Value.Count,
                                             kv.Value.TotalJitMs, kv.Value.MaxJitMs,
                                             kv.Value.ILSize, kv.Value.NativeSize))
            .ToList();

        var topByCount = c.ByMethod
            .OrderByDescending(kv => kv.Value.Count)
            .Take(top)
            .Select(kv => new JitMethodEntry(kv.Key, kv.Value.Module, kv.Value.Count,
                                             kv.Value.TotalJitMs, kv.Value.MaxJitMs,
                                             kv.Value.ILSize, kv.Value.NativeSize))
            .ToList();

        var topModules = c.ByModule
            .OrderByDescending(kv => kv.Value.TotalJitMs > 0 ? kv.Value.TotalJitMs : kv.Value.MethodCount)
            .Take(20)
            .Select(kv => new JitModuleSummary(kv.Key, kv.Value.MethodCount, kv.Value.TotalJitMs))
            .ToList();

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {total:N0} methods JIT-compiled  •  {c.ByMethod.Count:N0} unique";

        double avgJitMs = total > 0 ? totalJitMs / c.ByMethod.Count : 0;

        return new JitTraceData(info, processFilter,
            total, totalJitMs, maxJitMs, avgJitMs,
            topByTime, topByCount, topModules, c.TimingAvailable);
    }

    public JitTraceData Analyze(TraceLog trace, string traceFileName, int top = 30,
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
            return new JitTraceData($"Failed during parse: {ex.Message}", processFilter, 0, 0, 0, 0, [], [], [], false);
        }
    }

    // Build a stable key from methodId xor'd with thread to correlate start/stop pairs.
    // We use methodId (MethodID field) as the primary key; fall back to thread.
    private static long BuildKey(TraceEvent ev)
    {
        long methodId = 0;
        try
        {
            var raw = ev.PayloadByName("MethodID");
            if (raw is not null) methodId = Convert.ToInt64(raw);
        }
        catch { /* ignored */ }
        return (methodId != 0) ? methodId : ((long)ev.ThreadID << 32);
    }

    private static string FullName(TraceEvent ev)
    {
        string ns     = SafeStr(ev, "MethodNamespace");
        string name   = SafeStr(ev, "MethodName");
        string sig    = SafeStr(ev, "MethodSignature");

        if (name.Length == 0) return "";

        string full = ns.Length > 0 ? $"{ns}.{name}" : name;
        // MethodSignature from TraceEvent starts with the return type, e.g. "instance void  (params)".
        // Append with a single space so the display reads "MethodName instance void  (...)".
        if (sig.Length > 0 && sig.Length < 80) full = $"{full} {sig.TrimStart()}";
        return full;
    }

    // Derive a readable module name from a JIT event.
    // Primary source: ModuleILPath payload (strip to filename without extension).
    // Fallback: infer from MethodNamespace — take the first two dot-separated
    // segments (e.g. "System.Collections" from "System.Collections.Generic").
    // This produces a human-readable approximation that is far more useful than "(unknown)".
    private static string ModuleName(TraceEvent ev)
    {
        string path = SafeStr(ev, "ModuleILPath");
        if (path.Length > 0)
            return Path.GetFileNameWithoutExtension(path);

        // Try alternate field names used by some providers
        string alt = SafeStr(ev, "ModuleILFileName");
        if (alt.Length > 0)
            return Path.GetFileNameWithoutExtension(alt);

        // Infer from namespace
        string ns = SafeStr(ev, "MethodNamespace");
        if (ns.Length > 0)
        {
            // Strip generic arity suffix `1 etc.
            int backtick = ns.IndexOf('`');
            if (backtick > 0) ns = ns[..backtick];

            var parts = ns.Split('.');
            // Use first two segments: "System.Collections", "Microsoft.EntityFrameworkCore", etc.
            return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : parts[0];
        }

        return "(unknown)";
    }


    private sealed class MethodAcc
    {
        public MethodAcc(string module, int ilSize, int nativeSize)
        { Module = module; ILSize = ilSize; NativeSize = nativeSize; }
        public string Module;
        public int    ILSize;
        public int    NativeSize;
        public int    Count;
        public double TotalJitMs;
        public double MaxJitMs;
    }

    private sealed class ModuleAcc
    {
        public int    MethodCount;
        public double TotalJitMs;
    }
}
