using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

public sealed class DeadlockAnalyzer
{
    public DeadlockData Analyze(DumpContext ctx, int minThreads = 2)
    {
        var threads     = ctx.Runtime.Threads.ToList();
        var threadNames = ThreadAnalysisAnalyzer.BuildThreadNameMap(ctx);
        var blocked     = ScanBlockedThreads(threads, threadNames);
        var groups      = BuildGroups(blocked, minThreads);

        return new DeadlockData(blocked, groups, threads.Count, NamedThreadCount: threadNames.Count);
    }

    private static List<BlockedThreadEntry> ScanBlockedThreads(
        List<ClrThread> threads, Dictionary<int, string> threadNames)
    {
        var blocked = new List<BlockedThreadEntry>();
        foreach (var t in threads)
        {
            try
            {
                var frames = t.EnumerateStackTrace().Take(10).ToList();
                var bf = frames.FirstOrDefault(f => IsBlockingFrame(f.Method?.Name ?? string.Empty));
                if (bf is null) continue;

                string blockType  = ExtractTypeName(bf.FrameName ?? bf.Method?.Signature ?? "");
                string blockFrame = bf.FrameName ?? bf.Method?.Signature ?? "<unknown>";
                var frameNames    = frames
                    .Select(f => f.FrameName ?? f.Method?.Signature ?? "<unknown>")
                    .ToList();

                threadNames.TryGetValue(t.ManagedThreadId, out string? name);
                blocked.Add(new BlockedThreadEntry(
                    ManagedId:   t.ManagedThreadId,
                    OSThreadId:  t.OSThreadId,
                    ThreadName:  name,
                    BlockType:   blockType,
                    BlockFrame:  blockFrame,
                    StackFrames: frameNames));
            }
            catch { }
        }
        return blocked;
    }

    private static List<ContentionGroup> BuildGroups(List<BlockedThreadEntry> blocked, int minThreads)
    {
        return blocked
            .GroupBy(b => b.BlockType)
            .Where(g => g.Count() >= minThreads)
            .OrderByDescending(g => g.Count())
            .Select(g => new ContentionGroup(
                LockType:      g.Key,
                ThreadIds:     g.Select(b => b.ManagedId).ToList(),
                TopBlockFrame: g.First().BlockFrame))
            .ToList();
    }

    private static bool IsBlockingFrame(string methodName) =>
        methodName is "WaitOne" or "Wait" or "Enter" or "TryEnter" or "Join"
                   or "Acquire" or "WaitAsync" or "GetResult" or "WaitAll" or "WaitAny"
        || methodName.Contains("Wait",  StringComparison.OrdinalIgnoreCase)
        || methodName.Contains("Sleep", StringComparison.OrdinalIgnoreCase);

    private static string ExtractTypeName(string frameOrSig)
    {
        // "System.Threading.Monitor.Enter(object, ref bool)" → "System.Threading.Monitor"
        int paren = frameOrSig.IndexOf('(');
        string before = paren > 0 ? frameOrSig[..paren] : frameOrSig;
        int lastDot = before.LastIndexOf('.');
        return lastDot > 0 ? before[..lastDot] : before;
    }
}
