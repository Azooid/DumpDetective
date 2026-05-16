using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Memory.Analyzers;

/// <summary>
/// Walks all managed thread stacks and identifies threads currently executing
/// inside native/runtime frames (P/Invoke transitions, CLR helper stubs, GC
/// coordination frames, interop call sites).
/// A large number of threads stuck at the same native call site indicates
/// a native-level bottleneck (shared native lock, pipe, socket, etc.).
/// </summary>
public sealed class NativeInteropAnalyzer
{
    // Omnipresent bookkeeping frames that carry no diagnostic signal.
    private static readonly HashSet<string> _boring = new(StringComparer.OrdinalIgnoreCase)
    {
        "GCFrame", "DebuggerSecurityCodeMarkFrame", "DebuggerU2MCatchHandlerFrame",
        "ExceptionFrame", "SecurityFrame", "TransitionFrame",
    };

    public NativeInteropData Analyze(DumpContext ctx)
    {
        var threads    = ctx.Runtime.Threads.ToList();
        var result     = new List<NativeThreadEntry>(threads.Count / 4);
        var frameTally = new Dictionary<string, int>(StringComparer.Ordinal);

        CommandBase.RunStatus($"Scanning native frames ({threads.Count} threads)...", update =>
        {
            int done = 0;
            foreach (var t in threads)
            {
                if ((++done & 0xF) == 0)
                    update($"Scanning native frames — {done}/{threads.Count} threads  •  {result.Count} with native frames");

                var    frames      = new List<NativeStackFrame>(16);
                int    nativeCount = 0;
                string deepest     = string.Empty;

                try
                {
                    foreach (var f in t.EnumerateStackTrace().Take(40))
                    {
                        bool   isNative = f.Kind == ClrStackFrameKind.Runtime;
                        string name     = f.FrameName ?? f.Method?.Signature ?? string.Empty;
                        if (string.IsNullOrEmpty(name)) continue;

                        frames.Add(new NativeStackFrame(name, isNative));

                        if (isNative && !IsBoring(name))
                        {
                            nativeCount++;
                            if (deepest.Length == 0) deepest = name;
                            frameTally.TryGetValue(name, out int prev);
                            frameTally[name] = prev + 1;
                        }
                    }
                }
                catch { /* corrupt/partial stacks are expected in crash dumps */ }

                if (nativeCount > 0)
                    result.Add(new NativeThreadEntry(
                        t.ManagedThreadId,
                        t.OSThreadId,
                        ClassifyThread(t),
                        frames,
                        nativeCount,
                        deepest));
            }
        });

        var topSites = frameTally
            .OrderByDescending(kv => kv.Value)
            .Take(25)
            .Select(kv => new NativeFrameSummary(kv.Key, kv.Value))
            .ToList();

        return new NativeInteropData(
            result.OrderByDescending(t => t.NativeFrameCount).ToList(),
            result.Count,
            topSites);
    }

    private static bool IsBoring(string name) =>
        _boring.Any(b => name.Contains(b, StringComparison.OrdinalIgnoreCase));

    private static string ClassifyThread(ClrThread t)
    {
        try
        {
            var methods = t.EnumerateStackTrace().Take(5).Select(f => f.Method?.Name ?? "").ToList();
            if (methods.Any(m => m.Contains("Finaliz", StringComparison.OrdinalIgnoreCase))) return "Finalizer";
            if (methods.Any(m => m is "GarbageCollect" or "Collect"))                        return "GC";
            if (methods.Any(m => m.Contains("Dispatch", StringComparison.OrdinalIgnoreCase))) return "ThreadPool";
            if (methods.Any(m => m.Contains("IOCP",    StringComparison.OrdinalIgnoreCase))) return "IOCP";
        }
        catch { }
        return t.IsAlive ? "Managed" : "Dead";
    }
}
