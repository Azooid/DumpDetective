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

    // ── Consumer — holds all per-event mutable state ─────────────────────────
    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;

        internal readonly Dictionary<string, AllocAcc>  AllocByType   = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, CallerAcc> CallerByFrame = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, LibAcc>    LibAlloc      = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, int>       LibCpu        = new(StringComparer.Ordinal);
        internal int  TotalAllocTicks;
        internal long TotalAllocBytes;
        internal long JsonAllocBytes;
        internal int  TotalCpuSamples;
        internal int  JsonCpuSamples;

        // Classification indices — each unique name classified once
        internal readonly Dictionary<string, string?> FrameLibIndex = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, string?> TypeLibIndex  = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, (bool IsAlloc, bool IsCpu)> EvTypeIndex
            = new(StringComparer.OrdinalIgnoreCase);

        public void Consume(TraceEvent ev, string evName, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            if (!EvTypeIndex.TryGetValue(evName, out var evType))
            {
                bool a =
                    evName.EndsWith("GCAllocationTick",  StringComparison.OrdinalIgnoreCase) ||
                    evName.EndsWith("GC/AllocationTick", StringComparison.OrdinalIgnoreCase) ||
                    evName.IndexOf("AllocationTick",     StringComparison.OrdinalIgnoreCase) >= 0;
                bool c =
                    evName.IndexOf("SampledProfile",  StringComparison.OrdinalIgnoreCase) >= 0 ||
                    evName.IndexOf("PerfInfo/Sample", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    evName.IndexOf("Kernel/PerfInfo", StringComparison.OrdinalIgnoreCase) >= 0;
                EvTypeIndex[evName] = evType = (a, c);
            }

            if (evType.IsAlloc)
            {
                TotalAllocTicks++;
                string typeName = SafeStr(ev, "TypeName");
                if (typeName.Length == 0) typeName = SafeStr(ev, "AllocationTypeName");
                if (typeName.Length == 0) typeName = "(unknown)";

                long bytes = SafeLong(ev, "AllocationAmount64");
                if (bytes <= 0) bytes = SafeLong(ev, "AllocationAmount");
                if (bytes <= 0) bytes = GcAllocTickSizeBytes;
                TotalAllocBytes += bytes;

                if (!TypeLibIndex.TryGetValue(typeName, out var library))
                    TypeLibIndex[typeName] = library = ClassifyTypeLibrary(typeName);
                if (library is null) return;

                JsonAllocBytes += bytes;
                if (!AllocByType.TryGetValue(typeName, out var tAcc))
                    AllocByType[typeName] = tAcc = new AllocAcc(library);
                tAcc.Ticks++;
                tAcc.Bytes += bytes;

                if (!LibAlloc.TryGetValue(library, out var lAcc))
                    LibAlloc[library] = lAcc = new LibAcc();
                lAcc.Ticks++;
                lAcc.Bytes += bytes;

                string callerFrame = FindAllocCaller(ev, typeName, FrameLibIndex);
                string callerKey   = $"{callerFrame}|{library}";
                if (!CallerByFrame.TryGetValue(callerKey, out var cAcc))
                    CallerByFrame[callerKey] = cAcc = new CallerAcc(callerFrame, library);
                cAcc.AllocTicks++;
                cAcc.AllocBytes += bytes;
                return;
            }

            if (!evType.IsCpu) return;

            TotalCpuSamples++;
            var callStack = ev.CallStack();
            if (callStack is null) return;

            string? hitLibrary     = null;
            string? callerFrameCpu = null;
            bool    inJsonZone     = false;

            for (var cur = callStack; cur is not null; cur = cur.Caller)
            {
                string method = cur.CodeAddress.FullMethodName ?? "";
                if (method.Length == 0) continue;

                if (!FrameLibIndex.TryGetValue(method, out var frameLibrary))
                    FrameLibIndex[method] = frameLibrary = ClassifyFrameLibrary(method);
                if (frameLibrary is not null)
                {
                    inJsonZone = true;
                    hitLibrary ??= frameLibrary;
                }
                else if (inJsonZone)
                {
                    callerFrameCpu = method;
                    break;
                }
            }

            if (hitLibrary is null) return;

            JsonCpuSamples++;
            LibCpu.TryGetValue(hitLibrary, out int prevCpu);
            LibCpu[hitLibrary] = prevCpu + 1;

            if (callerFrameCpu is not null)
            {
                string callerKey = $"{callerFrameCpu}|{hitLibrary}";
                if (!CallerByFrame.TryGetValue(callerKey, out var cAcc))
                    CallerByFrame[callerKey] = cAcc = new CallerAcc(callerFrameCpu, hitLibrary);
                cAcc.CpuSamples++;
            }
        }

        public bool WantsEvent(string eventName) => eventName.IndexOf("AllocationTick", StringComparison.OrdinalIgnoreCase) >= 0 || eventName.IndexOf("SampledProfile", StringComparison.OrdinalIgnoreCase) >= 0 || eventName.IndexOf("PerfInfo/Sample", StringComparison.OrdinalIgnoreCase) >= 0 || eventName.IndexOf("Kernel/PerfInfo", StringComparison.OrdinalIgnoreCase) >= 0;

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null) => new Consumer(processFilter);

    public JsonSerializationTraceData BuildResult(ITraceEventConsumer consumer, string traceFileName,
                                                   int top = 20, string? processFilter = null)
    {
        var c = (Consumer)consumer;
        bool hasData = c.TotalAllocTicks > 0 || c.TotalCpuSamples > 0;
        if (!hasData)
            return Empty($"{traceFileName}  |  No allocation or CPU sample events found — see collection guidance", processFilter);

        bool hasJson = c.JsonAllocBytes > 0 || c.JsonCpuSamples > 0;
        if (!hasJson)
        {
            string noJsonInfo = $"{traceFileName}" +
                (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                $"  |  {c.TotalCpuSamples:N0} CPU samples  •  {c.TotalAllocTicks:N0} alloc ticks  •  no JSON frames detected";
            return new JsonSerializationTraceData(noJsonInfo, processFilter,
                c.TotalAllocTicks, 0, c.TotalAllocBytes,
                c.TotalCpuSamples, 0, 0,
                [], [], [], HasData: true);
        }

        double jsonCpuPct = c.TotalCpuSamples > 0
            ? c.JsonCpuSamples * 100.0 / c.TotalCpuSamples
            : 0;

        var allLibraries = new HashSet<string>(c.LibAlloc.Keys, StringComparer.Ordinal);
        foreach (var k in c.LibCpu.Keys) allLibraries.Add(k);

        var byLibrary = allLibraries
            .Select(lib =>
            {
                c.LibAlloc.TryGetValue(lib, out var lAlloc);
                c.LibCpu.TryGetValue(lib, out int lCpu);
                double libCpuPct = c.TotalCpuSamples > 0 ? lCpu * 100.0 / c.TotalCpuSamples : 0;
                return new JsonLibrarySummary(lib,
                    lAlloc?.Bytes ?? 0, lAlloc?.Ticks ?? 0, lCpu, libCpuPct);
            })
            .OrderByDescending(l => l.AllocBytes + l.CpuSamples * GcAllocTickSizeBytes)
            .ToList();

        var topTypes = c.AllocByType
            .OrderByDescending(kv => kv.Value.Bytes)
            .Take(top)
            .Select(kv => new JsonTypeAllocSummary(
                kv.Key, kv.Value.Library, kv.Value.Ticks, kv.Value.Bytes,
                c.JsonAllocBytes > 0 ? kv.Value.Bytes * 100.0 / c.JsonAllocBytes : 0))
            .ToList();

        var topCallers = c.CallerByFrame.Values
            .OrderByDescending(ca => ca.AllocBytes + ca.CpuSamples * GcAllocTickSizeBytes)
            .Take(top)
            .Select(ca => new JsonCallerSummary(
                ca.Frame, ca.Library, ca.AllocBytes, ca.AllocTicks, ca.CpuSamples))
            .ToList();

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  JSON CPU: {jsonCpuPct:F1}%  •  JSON alloc: ~{FormatBytes(c.JsonAllocBytes)}";

        return new JsonSerializationTraceData(
            info, processFilter,
            c.TotalAllocTicks, c.JsonAllocBytes, c.TotalAllocBytes,
            c.TotalCpuSamples, c.JsonCpuSamples, jsonCpuPct,
            topTypes, topCallers, byLibrary, HasData: true);
    }

    public JsonSerializationTraceData Analyze(TraceLog trace, string traceFileName,
                                               int top = 20, string? processFilter = null,
                                               Action<string>? progress = null)
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
            return Empty($"Failed during event pass: {ex.Message}", processFilter);
        }
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
    private static string FindAllocCaller(TraceEvent ev, string allocTypeName,
                                           Dictionary<string, string?> frameLibIndex)
    {
        var cs = ev.CallStack();
        if (cs is null) return "(no call stack)";

        var cur = cs;
        while (cur is not null)
        {
            string? name = cur.CodeAddress.FullMethodName;
            if (string.IsNullOrEmpty(name)) { cur = cur.Caller; continue; }

            // Use shared frame index — each unique method name classified once
            if (!frameLibIndex.TryGetValue(name, out var lib))
                frameLibIndex[name] = lib = ClassifyFrameLibrary(name);

            if (lib is not null)      { cur = cur.Caller; continue; } // JSON frame — skip
            if (IsRuntimeFrame(name)) { cur = cur.Caller; continue; }

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
