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

    public JitTraceData Analyze(TraceLog trace, string traceFileName, int top = 30,
                                 string? processFilter = null, Action<string>? progress = null)
    {
        // Track in-progress compilations keyed by (threadId, methodId) to match start→stop pairs
        // We also track a flat record per method name for aggregation.
        var inFlight  = new Dictionary<long, (double startMs, string method, string module, int ilSize)>();
        var byMethod  = new Dictionary<string, MethodAcc>(StringComparer.Ordinal);
        var byModule  = new Dictionary<string, ModuleAcc>(StringComparer.OrdinalIgnoreCase);
        bool timingAvailable = false;
        long evTotal = trace.EventCount;
        long evProcessed = 0;
        long lastProgressMs = 0;

        try
        {
            foreach (var ev in trace.Events)
            {
                evProcessed++;
                if (progress is not null && Environment.TickCount64 - lastProgressMs >= 200)
                {
                    progress($"{byMethod.Count:N0} methods JIT'd");
                    lastProgressMs = Environment.TickCount64;
                }
                if (processFilter is not null &&
                    !(ev.ProcessName ?? "").Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";

                // ── JIT start: Method/JittingStarted or MethodJitInliningSucceeded ──────────
                if (evName.IndexOf("JittingStarted", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    evName.IndexOf("MethodJitStart", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    long   key    = BuildKey(ev);
                    string method = FullName(ev);
                    string module = ModuleName(ev);
                    int    ilSize = SafeInt(ev, "MethodILSize");

                    inFlight[key] = (ev.TimeStampRelativeMSec, method, module, ilSize);
                    continue;
                }

                // ── JIT end: Method/LoadVerbose (fired after JIT completes) ─────────────────
                if (evName.IndexOf("MethodLoadVerbose", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    evName.IndexOf("Method/LoadVerbose", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    evName.IndexOf("MethodLoad/Verbose", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    long key = BuildKey(ev);
                    int  nativeSize = SafeInt(ev, "MethodSize");
                    string method = FullName(ev);
                    string module = ModuleName(ev);

                    double jitMs = 0;
                    int    ilSize = 0;
                    if (inFlight.TryGetValue(key, out var start))
                    {
                        jitMs  = ev.TimeStampRelativeMSec - start.startMs;
                        ilSize = start.ilSize;
                        if (start.method.Length > 0) method = start.method;
                        if (start.module.Length > 0) module = start.module;
                        inFlight.Remove(key);
                        timingAvailable = true;
                    }

                    if (method.Length == 0) method = "(unknown)";
                    // module is always non-empty — ModuleName() guarantees a fallback via namespace inference

                    if (!byMethod.TryGetValue(method, out var macc))
                        byMethod[method] = macc = new MethodAcc(module, ilSize, nativeSize);
                    macc.Count++;
                    macc.TotalJitMs += jitMs;
                    if (jitMs > macc.MaxJitMs) macc.MaxJitMs = jitMs;

                    if (!byModule.TryGetValue(module, out var modacc))
                        byModule[module] = modacc = new ModuleAcc();
                    modacc.MethodCount++;
                    modacc.TotalJitMs += jitMs;
                    continue;
                }

                // ── MethodLoad (non-verbose): only has method address, no timing, still count ──
                if (evName.IndexOf("MethodLoad", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    evName.IndexOf("Verbose", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    string method = FullName(ev);
                    string module = ModuleName(ev);
                    if (method.Length == 0) continue;
                    if (!byMethod.TryGetValue(method, out var macc))
                        byMethod[method] = macc = new MethodAcc(module, 0, 0);
                    macc.Count++;

                    if (!byModule.TryGetValue(module, out var modacc))
                        byModule[module] = modacc = new ModuleAcc();
                    modacc.MethodCount++;
                }
            }
        }
        catch (Exception ex)
        {
            return new JitTraceData($"Failed during parse: {ex.Message}", processFilter, 0, 0, 0, 0, [], [], [], false);
        }

        if (byMethod.Count == 0)
        {
            return new JitTraceData(
                $"{traceFileName}  |  0 JIT events — collect with --providers Microsoft-Windows-DotNETRuntime:0x10:5",
                processFilter, 0, 0, 0, 0, [], [], [], false);
        }

        double totalJitMs = byMethod.Values.Sum(a => a.TotalJitMs);
        double maxJitMs   = byMethod.Values.Max(a => a.MaxJitMs);
        int    total      = byMethod.Values.Sum(a => a.Count);

        var topByTime = byMethod
            .Where(kv => kv.Value.TotalJitMs > 0)
            .OrderByDescending(kv => kv.Value.TotalJitMs)
            .Take(top)
            .Select(kv => new JitMethodEntry(kv.Key, kv.Value.Module, kv.Value.Count,
                                             kv.Value.TotalJitMs, kv.Value.MaxJitMs,
                                             kv.Value.ILSize, kv.Value.NativeSize))
            .ToList();

        var topByCount = byMethod
            .OrderByDescending(kv => kv.Value.Count)
            .Take(top)
            .Select(kv => new JitMethodEntry(kv.Key, kv.Value.Module, kv.Value.Count,
                                             kv.Value.TotalJitMs, kv.Value.MaxJitMs,
                                             kv.Value.ILSize, kv.Value.NativeSize))
            .ToList();

        var topModules = byModule
            .OrderByDescending(kv => kv.Value.TotalJitMs > 0 ? kv.Value.TotalJitMs : kv.Value.MethodCount)
            .Take(20)
            .Select(kv => new JitModuleSummary(kv.Key, kv.Value.MethodCount, kv.Value.TotalJitMs))
            .ToList();

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {total:N0} methods JIT-compiled  •  {byMethod.Count:N0} unique";

        double avgJitMs = total > 0 ? totalJitMs / byMethod.Count : 0;

        return new JitTraceData(info, processFilter,
            total, totalJitMs, maxJitMs, avgJitMs,
            topByTime, topByCount, topModules, timingAvailable);
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

    private static string SafeStr(TraceEvent ev, string field)
    {
        try { return ev.PayloadByName(field)?.ToString() ?? ""; } catch { return ""; }
    }

    private static int SafeInt(TraceEvent ev, string field)
    {
        try
        {
            var raw = ev.PayloadByName(field);
            return raw is not null ? Convert.ToInt32(raw) : 0;
        }
        catch { return 0; }
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
