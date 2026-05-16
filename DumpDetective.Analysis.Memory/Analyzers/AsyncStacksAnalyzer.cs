using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Memory.Analyzers;

/// <summary>
/// Collects all heap-resident async state-machine objects, recording the outer
/// method name (<c>meta.AsyncMethod</c>), suspension state, and address.
/// </summary>
public sealed class AsyncStacksAnalyzer : IHeapObjectConsumer
{
    private List<StateMachineEntry>? _entries;
    private int _backlogTotal;
    private AsyncStacksData? _result;

    public AsyncStacksData? Result => _result;

    internal void Reset()
    {
        _entries      = new List<StateMachineEntry>();
        _backlogTotal = 0;
        _result       = null;
    }

    // ── IHeapObjectConsumer ───────────────────────────────────────────────────

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        var method = meta.AsyncMethod;
        if (method is null || _entries is null) return;

        string stateLabel = ReadStateLabel(obj);
        _entries.Add(new StateMachineEntry(method, stateLabel, obj.Address));

        // Only count suspended (Awaiting) state machines in the backlog.
        // Completed (-1) and Initial (-2) state machines must not inflate the count.
        if (stateLabel == "Awaiting")
            _backlogTotal++;
    }

    public void OnWalkComplete()
    {
        var entries = (_entries ?? []).ToList();
        _result = new AsyncStacksData(entries, _backlogTotal, BuildDepChains(entries));
    }

    // ── Dependency chain inference ────────────────────────────────────────────

    private static IReadOnlyList<AsyncDepChain> BuildDepChains(List<StateMachineEntry> entries)
    {
        // Only consider suspended (Awaiting) state machines for chain building.
        var suspended = entries.Where(e => e.State == "Awaiting").ToList();
        if (suspended.Count == 0) return [];

        // Group by the class/namespace prefix (everything before the last '+' or last method separator).
        // e.g. "MyApp.OrderService+<GetOrderAsync>d__12" → prefix "MyApp.OrderService"
        var byClass = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var e in suspended)
        {
            string cls = ExtractClassName(e.Method);
            if (!byClass.TryGetValue(cls, out var list))
                byClass[cls] = list = new List<string>();
            if (!list.Contains(e.Method))
                list.Add(e.Method);
        }

        var chains = new List<AsyncDepChain>(byClass.Count);
        foreach (var (cls, methods) in byClass)
        {
            if (methods.Count == 0) continue;
            // Heuristic: sort methods by name length — shorter names tend to be callers,
            // longer compiler-generated names tend to be callees.
            methods.Sort(static (a, b) => a.Length.CompareTo(b.Length));
            bool likelyIo = methods.Any(m =>
                m.Contains("Sql",     StringComparison.OrdinalIgnoreCase) ||
                m.Contains("Http",    StringComparison.OrdinalIgnoreCase) ||
                m.Contains("Socket",  StringComparison.OrdinalIgnoreCase) ||
                m.Contains("Stream",  StringComparison.OrdinalIgnoreCase) ||
                m.Contains("Read",    StringComparison.OrdinalIgnoreCase) ||
                m.Contains("Write",   StringComparison.OrdinalIgnoreCase) ||
                m.Contains("Connect", StringComparison.OrdinalIgnoreCase));
            int count = suspended.Count(e => ExtractClassName(e.Method) == cls);
            chains.Add(new AsyncDepChain(cls, methods, count, likelyIo));
        }

        // Return top 20 chains by instance count descending.
        chains.Sort(static (a, b) => b.InstanceCount.CompareTo(a.InstanceCount));
        return chains.Count > 20 ? chains[..20] : chains;
    }

    private static string ExtractClassName(string method)
    {
        // "MyApp.OrderService+<GetOrderAsync>d__12" → "MyApp.OrderService"
        int plus = method.IndexOf('+');
        if (plus > 0) return method[..plus];
        // Fallback: namespace up to the last dot
        int dot = method.LastIndexOf('.');
        return dot > 0 ? method[..dot] : method;
    }

    public IHeapObjectConsumer CreateClone()
    {
        var c = new AsyncStacksAnalyzer();
        c.Reset();
        return c;
    }

    public void MergeFrom(IHeapObjectConsumer other)
    {
        var src = (AsyncStacksAnalyzer)other;
        _backlogTotal += src._backlogTotal;
        if (src._entries is not null)
            (_entries ??= []).AddRange(src._entries);
    }

    // ── Command entry point ───────────────────────────────────────────────────

    public AsyncStacksData Analyze(DumpContext ctx)
    {
        if (ctx.GetAnalysis<AsyncStacksData>() is { } cached) return cached;

        Reset();
        CommandBase.RunStatus("Scanning async state machines...", update =>
            HeapWalker.Walk(ctx.Heap, [this], CommandBase.StatusProgress(update)));

        ctx.SetAnalysis(_result!);
        return _result!;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads <c>&lt;&gt;1__state</c>: −2 = Initial, −1 = Completed/Faulted, ≥0 = Awaiting.
    /// </summary>
    private static string ReadStateLabel(in ClrObject obj)
    {
        try
        {
            var field = obj.Type?.GetFieldByName("<>1__state");
            if (field is null) return "Unknown";
            int state = field.Read<int>(obj, interior: false);
            return state switch
            {
                -2 => "Initial",
                -1 => "Completed",
                 _ => "Awaiting",
            };
        }
        catch { return "Unknown"; }
    }
}
