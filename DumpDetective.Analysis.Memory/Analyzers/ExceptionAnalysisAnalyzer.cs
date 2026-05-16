using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DumpDetective.Analysis.Memory.Analyzers;

/// <summary>
/// Collects heap-resident exception objects by type, capturing a sample of up to
/// <c>MaxPerType</c> instances per type with message, HResult, inner type, and stack trace.
/// Thread-correlation (IsActive / ThreadId) is NOT done here — commands do that separately
/// from <c>ctx.Runtime.Threads</c>.
/// </summary>
public sealed class ExceptionAnalysisAnalyzer : IHeapObjectConsumer
{
    private const int MaxPerType = 10;

    private Dictionary<string, List<ExceptionHeapRecord>>? _byType;
    private Dictionary<string, int>? _totals;
    private int _totalAll;
    private ExceptionAnalysisData? _result;

    public ExceptionAnalysisData? Result => _result;

    internal void Reset()
    {
        _byType   = new Dictionary<string, List<ExceptionHeapRecord>>(64, StringComparer.Ordinal);
        _totals   = new Dictionary<string, int>(64, StringComparer.Ordinal);
        _totalAll = 0;
        _result   = null;
    }

    // ── IHeapObjectConsumer ───────────────────────────────────────────────────

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        if (!meta.IsException || _byType is null || _totals is null) return;

        var typeName = meta.Name;
        _totalAll++;

        ref int tc = ref CollectionsMarshal.GetValueRefOrAddDefault(_totals, typeName, out _);
        tc++;

        if (!_byType.TryGetValue(typeName, out var list))
        {
            list = new List<ExceptionHeapRecord>(capacity: MaxPerType);
            _byType[typeName] = list;
        }

        if (list.Count < MaxPerType)
            list.Add(ExtractRecord(obj, typeName));
    }

    public void OnWalkComplete()
    {
        if (_byType is null || _totals is null)
        {
            _result = new ExceptionAnalysisData(
                new Dictionary<string, ExceptionTypeGroup>(),
                new Dictionary<string, int>(),
                0);
            return;
        }

        var byTypeResult = new Dictionary<string, ExceptionTypeGroup>(_byType.Count, StringComparer.Ordinal);
        foreach (var kv in _byType)
            byTypeResult[kv.Key] = new ExceptionTypeGroup(kv.Key, kv.Value.ToList());

        _result = new ExceptionAnalysisData(byTypeResult, _totals, _totalAll,
            DetectFatalPrecursors(_totals));
    }

    public IHeapObjectConsumer CreateClone()
    {
        var c = new ExceptionAnalysisAnalyzer();
        c.Reset();
        return c;
    }

    public void MergeFrom(IHeapObjectConsumer other)
    {
        var src = (ExceptionAnalysisAnalyzer)other;
        _totalAll += src._totalAll;
        if (src._totals is not null)
        {
            _totals ??= new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (name, count) in src._totals)
            {
                ref int dst = ref System.Runtime.InteropServices.CollectionsMarshal
                    .GetValueRefOrAddDefault(_totals, name, out _);
                dst += count;
            }
        }
        if (src._byType is not null)
        {
            _byType ??= new Dictionary<string, List<ExceptionHeapRecord>>(StringComparer.Ordinal);
            foreach (var (typeName, records) in src._byType)
            {
                if (!_byType.TryGetValue(typeName, out var dst))
                    _byType[typeName] = dst = new List<ExceptionHeapRecord>(MaxPerType);
                foreach (var r in records)
                    if (dst.Count < MaxPerType) dst.Add(r);
            }
        }
    }

    // ── Command entry point ───────────────────────────────────────────────────

    public ExceptionAnalysisData Analyze(DumpContext ctx)
    {
        if (ctx.GetAnalysis<ExceptionAnalysisData>() is { } cached) return cached;

        Reset();
        CommandBase.RunStatus("Scanning exception objects...", update =>
            HeapWalker.Walk(ctx.Heap, [this], CommandBase.StatusProgress(update)));

        ctx.SetAnalysis(_result!);
        return _result!;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ExceptionHeapRecord ExtractRecord(in ClrObject obj, string typeName)
    {
        string  msg     = TryReadString(obj, "_message",        maxLength: 120);
        int     hresult = TryReadInt(obj, "_HResult");
        string? inner   = TryReadObjectTypeName(obj, "_innerException");
        // Try the pre-formatted string first (populated when .StackTrace was accessed),
        // then fall back to ClrMD's structured frame reader from the _stackTrace byte array,
        // which works even when _stackTraceString was never materialised.
        var frames = TryReadStackFrames(obj, "_stackTraceString");
        if (frames.Count == 0)
            frames = TryReadFramesViaClrMD(obj);
        string  hash    = ComputeStackHash(frames);
        return new ExceptionHeapRecord(obj.Address, typeName, msg, hresult, inner, frames, hash);
    }

    private static string ComputeStackHash(IReadOnlyList<string> frames)
    {
        if (frames.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var f in frames)
        {
            if (!string.IsNullOrWhiteSpace(f))
            {
                sb.Append(f);
                sb.Append('|');
            }
        }
        if (sb.Length == 0) return "";
        Span<byte> hashBytes = stackalloc byte[4];
        var data = Encoding.UTF8.GetBytes(sb.ToString());
        // Use last 4 bytes of SHA256 as an 8-hex-char fingerprint.
        var full = SHA256.HashData(data);
        return Convert.ToHexString(full.AsSpan(28, 4)).ToLowerInvariant();
    }

    private static FatalPrecursorFlags DetectFatalPrecursors(Dictionary<string, int> totals)
    {
        bool oom     = false, soe  = false, av    = false,
             abort   = false, init = false, invoc = false;
        foreach (var name in totals.Keys)
        {
            if (name.Contains("OutOfMemoryException",       StringComparison.Ordinal)) oom   = true;
            if (name.Contains("StackOverflowException",     StringComparison.Ordinal)) soe   = true;
            if (name.Contains("AccessViolationException",   StringComparison.Ordinal)) av    = true;
            if (name.Contains("ThreadAbortException",       StringComparison.Ordinal)) abort = true;
            if (name.Contains("TypeInitializationException",StringComparison.Ordinal)) init  = true;
            if (name.Contains("TargetInvocationException",  StringComparison.Ordinal)) invoc = true;
        }
        return new FatalPrecursorFlags(oom, soe, av, abort, init, invoc);
    }

    private static string TryReadString(in ClrObject obj, string fieldName, int maxLength = -1)
    {
        try
        {
            var field = obj.Type?.GetFieldByName(fieldName);
            if (field is null) return "";
            var value = field.ReadObject(obj, interior: false);
            return value.IsValid ? (value.AsString(maxLength) ?? "") : "";
        }
        catch { return ""; }
    }

    private static int TryReadInt(in ClrObject obj, string fieldName)
    {
        try
        {
            var field = obj.Type?.GetFieldByName(fieldName);
            return field is not null ? field.Read<int>(obj, interior: false) : 0;
        }
        catch { return 0; }
    }

    private static string? TryReadObjectTypeName(in ClrObject obj, string fieldName)
    {
        try
        {
            var field = obj.Type?.GetFieldByName(fieldName);
            if (field is null) return null;
            var innerObj = field.ReadObject(obj, interior: false);
            return innerObj.IsNull || !innerObj.IsValid ? null : innerObj.Type?.Name;
        }
        catch { return null; }
    }

    private static IReadOnlyList<string> TryReadStackFrames(in ClrObject obj, string fieldName)
    {
        try
        {
            var field = obj.Type?.GetFieldByName(fieldName);
            if (field is null) return [];
            var value = field.ReadObject(obj, interior: false);
            if (!value.IsValid) return [];
            string? raw = value.AsString();
            if (string.IsNullOrEmpty(raw)) return [];
            return [.. raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)];
        }
        catch { return []; }
    }

    /// <summary>
    /// Uses ClrMD's structured exception reader to extract stack frames from the
    /// <c>_stackTrace</c> byte array inside the exception object. This works even
    /// when <c>_stackTraceString</c> was never materialised (i.e. <c>.StackTrace</c>
    /// was never accessed before the dump was taken).
    /// </summary>
    private static IReadOnlyList<string> TryReadFramesViaClrMD(in ClrObject obj)
    {
        try
        {
            var ex = obj.AsException();
            if (ex is null) return [];
            var stackTrace = ex.StackTrace;
            if (stackTrace.IsDefaultOrEmpty) return [];
            var result = new List<string>(stackTrace.Length);
            foreach (var frame in stackTrace)
            {
                string label = frame.Method?.Signature ?? frame.Method?.Name ?? frame.FrameName ?? "";
                if (!string.IsNullOrWhiteSpace(label))
                    result.Add(label);
            }
            return result;
        }
        catch { return []; }
    }
}
