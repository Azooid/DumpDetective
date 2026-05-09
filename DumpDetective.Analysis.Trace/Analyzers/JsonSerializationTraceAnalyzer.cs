using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Measures JSON serialization cost from two complementary ETW signals:
///
///   1. GCAllocationTick — identifies <c>System.Text.Json.*</c>, <c>Newtonsoft.Json.*</c>,
///      and <c>System.Runtime.Serialization.Json.*</c> types allocated during the trace.
///      Each tick fires every ~100 KB allocated, so byte totals are sampled estimates.
///
///   2. CPU samples (SampledProfile / PerfInfo) — walks every call stack and records any
///      sample where a JSON library frame appears anywhere in the stack.  This gives the
///      inclusive CPU percentage attributable to JSON work.
///
/// Required provider strings for the best signal:
///   dotnet-trace:
///     --providers 'Microsoft-Windows-DotNETRuntime:0x1:5'   (allocation ticks, verbose)
///     --profile   cpu-sampling                               (CPU samples)
///   PerfView:
///     /Providers:"Microsoft-Windows-DotNETRuntime" /GCOnly  (allocation)
///     /KernelEvents:Profile                                  (CPU)
/// </summary>
public sealed class JsonSerializationTraceAnalyzer
{
    private const long TickSizeBytes = 100_000; // ~100 KB per GCAllocationTick

    // ── Well-known JSON type prefixes / short names ──────────────────────────
    // Used against GCAllocationTick TypeName field.
    private static readonly string[] s_jsonTypePrefixes =
    [
        "System.Text.Json.",
        "Newtonsoft.Json.",
        "System.Runtime.Serialization.Json.",
    ];

    private static readonly string[] s_jsonTypeShortNames =
    [
        "Utf8JsonWriter",
        "Utf8JsonReader",
        "JsonDocument",
        "JsonElement",
        "JsonProperty",
        "JsonNode",
        "JsonArray",
        "JsonObject",
        "JsonValue",
        "PooledByteBufferWriter",
        "ArrayBufferWriter",   // used internally by STJ
    ];

    // ── CPU frame substring matches ──────────────────────────────────────────
    // Used against FullMethodName in CPU call stacks.
    private static readonly string[] s_jsonFrameSubstrings =
    [
        "System.Text.Json",
        "Newtonsoft.Json",
        "JsonSerializer",
        "DataContractJsonSerializer",
        "System.Runtime.Serialization.Json",
        "Utf8JsonWriter",
        "Utf8JsonReader",
    ];

    public JsonSerializationTraceData Analyze(string tracePath, int top = 20,
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
            return Empty($"Failed: {ex.Message}", processFilter);
        }
    }

    public JsonSerializationTraceData Analyze(TraceLog trace, string traceFileName,
                                               int top = 20, string? processFilter = null)
    {
        // Accumulators keyed by type name (alloc) or frame (CPU)
        var allocByType    = new Dictionary<string, AllocAcc>(StringComparer.Ordinal);
        var callerByFrame  = new Dictionary<string, CallerAcc>(StringComparer.OrdinalIgnoreCase);
        var libAlloc       = new Dictionary<string, LibAcc>(StringComparer.Ordinal);
        var libCpu         = new Dictionary<string, int>(StringComparer.Ordinal);

        int totalAllocTicks = 0;
        long totalAllocBytes = 0;
        long jsonAllocBytes  = 0;

        int totalCpuSamples = 0;
        int jsonCpuSamples  = 0;

        try
        {
            foreach (var ev in trace.Events)
            {
                if (processFilter is not null &&
                    !(ev.ProcessName ?? "").Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";

                // ── GCAllocationTick ───────────────────────────────────────────────────
                bool isAlloc =
                    evName.EndsWith("GCAllocationTick",  StringComparison.OrdinalIgnoreCase) ||
                    evName.EndsWith("GC/AllocationTick", StringComparison.OrdinalIgnoreCase) ||
                    evName.IndexOf("AllocationTick",     StringComparison.OrdinalIgnoreCase) >= 0;

                if (isAlloc)
                {
                    totalAllocTicks++;
                    string typeName  = SafeStr(ev, "TypeName");
                    if (typeName.Length == 0) typeName = SafeStr(ev, "AllocationTypeName");
                    if (typeName.Length == 0) typeName = "(unknown)";

                    long bytes = SafeLong(ev, "AllocationAmount64");
                    if (bytes <= 0) bytes = SafeLong(ev, "AllocationAmount");
                    if (bytes <= 0) bytes = TickSizeBytes;
                    totalAllocBytes += bytes;

                    string? library = ClassifyTypeLibrary(typeName);
                    if (library is null) continue; // not a JSON type

                    jsonAllocBytes += bytes;
                    if (!allocByType.TryGetValue(typeName, out var tAcc))
                        allocByType[typeName] = tAcc = new AllocAcc(library);
                    tAcc.Ticks++;
                    tAcc.Bytes += bytes;

                    if (!libAlloc.TryGetValue(library, out var lAcc))
                        libAlloc[library] = lAcc = new LibAcc();
                    lAcc.Ticks++;
                    lAcc.Bytes += bytes;

                    // Caller for allocation: innermost non-JSON, non-runtime user frame
                    string callerFrame = FindAllocCaller(ev, typeName);
                    string callerKey   = $"{callerFrame}|{library}";
                    if (!callerByFrame.TryGetValue(callerKey, out var cAcc))
                        callerByFrame[callerKey] = cAcc = new CallerAcc(callerFrame, library);
                    cAcc.AllocTicks++;
                    cAcc.AllocBytes += bytes;
                    continue;
                }

                // ── CPU sample ────────────────────────────────────────────────────────
                bool isCpu =
                    evName.IndexOf("SampledProfile",  StringComparison.OrdinalIgnoreCase) >= 0 ||
                    evName.IndexOf("PerfInfo/Sample", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    evName.IndexOf("Kernel/PerfInfo", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!isCpu) continue;

                totalCpuSamples++;
                var callStack = ev.CallStack();
                if (callStack is null) continue;

                // Walk the stack — find the deepest JSON frame and the first non-JSON
                // user caller above it.
                //
                // Performance: break as soon as we have both hitLibrary + callerFrameCpu
                // so we never walk all the way to the root once the caller is identified.
                // This is the main speedup — no depth cap is needed.
                string? hitLibrary     = null;
                string? callerFrameCpu = null;
                bool    inJsonZone     = false;

                for (var cur = callStack; cur is not null; cur = cur.Caller)
                {
                    string method = cur.CodeAddress.FullMethodName ?? "";
                    if (method.Length == 0) continue;

                    string? frameLibrary = ClassifyFrameLibrary(method);
                    if (frameLibrary is not null)
                    {
                        inJsonZone = true;
                        hitLibrary ??= frameLibrary;
                    }
                    else if (inJsonZone)
                    {
                        // First non-JSON frame above the JSON zone = the caller we want.
                        callerFrameCpu = method;
                        break; // have everything — no need to walk further
                    }
                }

                if (hitLibrary is null) continue; // no JSON frames in this sample

                jsonCpuSamples++;
                libCpu.TryGetValue(hitLibrary, out int prevCpu);
                libCpu[hitLibrary] = prevCpu + 1;

                if (callerFrameCpu is not null)
                {
                    string callerKey = $"{callerFrameCpu}|{hitLibrary}";
                    if (!callerByFrame.TryGetValue(callerKey, out var cAcc))
                        callerByFrame[callerKey] = cAcc = new CallerAcc(callerFrameCpu, hitLibrary);
                    cAcc.CpuSamples++;
                }
            }
        }
        catch (Exception ex)
        {
            return Empty($"Failed during event pass: {ex.Message}", processFilter);
        }

        bool hasData = totalAllocTicks > 0 || totalCpuSamples > 0;
        if (!hasData)
            return Empty($"{traceFileName}  |  No allocation or CPU sample events found — see collection guidance", processFilter);

        bool hasJson = jsonAllocBytes > 0 || jsonCpuSamples > 0;
        if (!hasJson)
        {
            string noJsonInfo = $"{traceFileName}" +
                (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                $"  |  {totalCpuSamples:N0} CPU samples  •  {totalAllocTicks:N0} alloc ticks  •  no JSON frames detected";
            return new JsonSerializationTraceData(noJsonInfo, processFilter,
                totalAllocTicks, 0, totalAllocBytes,
                totalCpuSamples, 0, 0,
                [], [], [], HasData: true);
        }

        double jsonCpuPct = totalCpuSamples > 0
            ? jsonCpuSamples * 100.0 / totalCpuSamples
            : 0;

        // ── Assemble per-library summaries ────────────────────────────────────
        var allLibraries = new HashSet<string>(libAlloc.Keys, StringComparer.Ordinal);
        foreach (var k in libCpu.Keys) allLibraries.Add(k);

        var byLibrary = allLibraries
            .Select(lib =>
            {
                libAlloc.TryGetValue(lib, out var lAlloc);
                libCpu.TryGetValue(lib, out int lCpu);
                double libCpuPct = totalCpuSamples > 0 ? lCpu * 100.0 / totalCpuSamples : 0;
                return new JsonLibrarySummary(lib,
                    lAlloc?.Bytes ?? 0, lAlloc?.Ticks ?? 0, lCpu, libCpuPct);
            })
            .OrderByDescending(l => l.AllocBytes + l.CpuSamples * TickSizeBytes)
            .ToList();

        // ── Top allocating types ──────────────────────────────────────────────
        var topTypes = allocByType
            .OrderByDescending(kv => kv.Value.Bytes)
            .Take(top)
            .Select(kv => new JsonTypeAllocSummary(
                kv.Key, kv.Value.Library, kv.Value.Ticks, kv.Value.Bytes,
                jsonAllocBytes > 0 ? kv.Value.Bytes * 100.0 / jsonAllocBytes : 0))
            .ToList();

        // ── Top callers ───────────────────────────────────────────────────────
        var topCallers = callerByFrame.Values
            .OrderByDescending(c => c.AllocBytes + c.CpuSamples * TickSizeBytes)
            .Take(top)
            .Select(c => new JsonCallerSummary(
                c.Frame, c.Library, c.AllocBytes, c.AllocTicks, c.CpuSamples))
            .ToList();

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  JSON CPU: {jsonCpuPct:F1}%  •  JSON alloc: ~{FormatBytes(jsonAllocBytes)}";

        return new JsonSerializationTraceData(
            info, processFilter,
            totalAllocTicks, jsonAllocBytes, totalAllocBytes,
            totalCpuSamples, jsonCpuSamples, jsonCpuPct,
            topTypes, topCallers, byLibrary, HasData: true);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Returns the library name if the type name belongs to a known JSON library; null otherwise.</summary>
    private static string? ClassifyTypeLibrary(string typeName)
    {
        foreach (var prefix in s_jsonTypePrefixes)
            if (typeName.StartsWith(prefix, StringComparison.Ordinal))
                return PrefixToLibrary(prefix);

        // Short-name match (without namespace) for known internal types
        int dot = typeName.LastIndexOf('.');
        string shortName = dot >= 0 ? typeName[(dot + 1)..] : typeName;
        foreach (var sn in s_jsonTypeShortNames)
            if (shortName.Equals(sn, StringComparison.Ordinal))
                return "System.Text.Json"; // all short-name matches belong to STJ

        return null;
    }

    private static string PrefixToLibrary(string prefix) => prefix switch
    {
        "System.Text.Json."                      => "System.Text.Json",
        "Newtonsoft.Json."                       => "Newtonsoft.Json",
        "System.Runtime.Serialization.Json."     => "DataContract JSON",
        _                                        => "Other JSON",
    };

    /// <summary>Returns the library name if the method frame belongs to a JSON library; null otherwise.</summary>
    private static string? ClassifyFrameLibrary(string method)
    {
        if (method.Contains("System.Text.Json",                  StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Utf8JsonWriter",                    StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Utf8JsonReader",                    StringComparison.OrdinalIgnoreCase))
            return "System.Text.Json";

        if (method.Contains("Newtonsoft.Json",                   StringComparison.OrdinalIgnoreCase))
            return "Newtonsoft.Json";

        if (method.Contains("JsonSerializer",                    StringComparison.OrdinalIgnoreCase) &&
            !method.Contains("Newtonsoft",                       StringComparison.OrdinalIgnoreCase))
            return "System.Text.Json"; // most likely STJ when unqualified

        if (method.Contains("DataContractJsonSerializer",        StringComparison.OrdinalIgnoreCase) ||
            method.Contains("System.Runtime.Serialization.Json", StringComparison.OrdinalIgnoreCase))
            return "DataContract JSON";

        return null;
    }

    /// <summary>Find the first user-code frame in the call stack that is NOT a JSON/runtime frame.</summary>
    private static string FindAllocCaller(TraceEvent ev, string allocTypeName)
    {
        var cs = ev.CallStack();
        if (cs is null) return "(no call stack)";

        var cur = cs;
        while (cur is not null)
        {
            string? name = cur.CodeAddress.FullMethodName;
            if (string.IsNullOrEmpty(name)) { cur = cur.Caller; continue; }

            // Skip JSON library frames and common runtime/GC frames
            if (ClassifyFrameLibrary(name) is not null)     { cur = cur.Caller; continue; }
            if (IsRuntimeFrame(name))                        { cur = cur.Caller; continue; }

            return name;
        }
        return "(no user frame)";
    }

    private static bool IsRuntimeFrame(string name) =>
        name.StartsWith("System.GC.",              StringComparison.Ordinal) ||
        name.StartsWith("System.Runtime.",         StringComparison.Ordinal) ||
        name.StartsWith("System.Threading.",       StringComparison.Ordinal) ||
        name.StartsWith("coreclr!",                StringComparison.Ordinal) ||
        name.StartsWith("clrjit!",                 StringComparison.Ordinal) ||
        name.StartsWith("ntdll!",                  StringComparison.Ordinal) ||
        name.StartsWith("KERNELBASE!",             StringComparison.Ordinal) ||
        name.StartsWith("[GC]",                    StringComparison.Ordinal);

    private static string SafeStr(TraceEvent ev, string field)
    {
        try { return ev.PayloadByName(field)?.ToString() ?? ""; } catch { return ""; }
    }

    private static long SafeLong(TraceEvent ev, string field)
    {
        try { return (long)Convert.ChangeType(ev.PayloadByName(field), typeof(long)); } catch { return 0; }
    }


    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576     => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024         => $"{bytes / 1_024.0:F1} KB",
        _                => $"{bytes} B",
    };

    private static JsonSerializationTraceData Empty(string info, string? filter) =>
        new(info, filter, 0, 0, 0, 0, 0, 0, [], [], [], HasData: false);

    // ─────────────────────────────────────────────────────────────────────────
    // Mutable accumulators (local to analyzer — not part of the data model)
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class AllocAcc(string library)
    {
        public readonly string Library = library;
        public int  Ticks;
        public long Bytes;
    }

    private sealed class CallerAcc(string frame, string library)
    {
        public readonly string Frame   = frame;
        public readonly string Library = library;
        public int  AllocTicks;
        public long AllocBytes;
        public int  CpuSamples;
    }

    private sealed class LibAcc
    {
        public int  Ticks;
        public long Bytes;
    }
}
