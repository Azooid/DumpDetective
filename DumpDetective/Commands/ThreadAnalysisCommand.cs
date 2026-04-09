using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class ThreadAnalysisCommand
{
    private const string Help = """
        Usage: DumpDetective thread-analysis <dump-file> [options]

        Options:
          -s, --stacks          Show top-5 stack frames per thread
          -b, --blocked-only    Show only threads that appear blocked
          -o, --output <file>   Write report to file (.md / .html / .txt)
          -h, --help            Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        bool showStacks = false, blockedOnly = false;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        foreach (var a in args)
        {
            if (a is "--stacks"       or "-s") showStacks  = true;
            if (a is "--blocked-only" or "-b") blockedOnly = true;
        }

        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, showStacks, blockedOnly));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, bool showStacks = false, bool blockedOnly = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);
        var threads = ctx.Runtime.Threads.ToList();

        var toShow = blockedOnly ? threads.Where(IsLikelyBlocked).ToList() : threads;

        sink.Section("Thread Summary");
        sink.KeyValues(
        [
            ("Total threads",   threads.Count.ToString("N0")),
            ("Likely blocked",  threads.Count(IsLikelyBlocked).ToString("N0")),
            ("With exception",  threads.Count(t => t.CurrentException is not null).ToString("N0")),
            ("GC cooperative",  threads.Count(t => t.GCMode == GCMode.Cooperative).ToString("N0")),
        ]);

        if (toShow.Count == 0) { sink.Text("No threads match the filter."); return; }

        sink.Section(blockedOnly ? $"Blocked Threads ({toShow.Count})" : $"All Threads ({toShow.Count})");
        var rows = new List<string[]>();
        foreach (var t in toShow)
        {
            string ex   = t.CurrentException?.Type?.Name ?? "";
            string mode = t.GCMode.ToString();
            if (!showStacks)
            {
                rows.Add([$"{t.ManagedThreadId}", $"{t.OSThreadId}", t.State.ToString(), mode, ex]);
                continue;
            }
            // With stacks: emit one row per thread header, then stack frames
            rows.Add([$"{t.ManagedThreadId}", $"{t.OSThreadId}", t.State.ToString(), mode, ex]);
            foreach (var f in t.EnumerateStackTrace().Take(5))
                rows.Add(["", "", f.FrameName ?? f.Method?.Signature ?? f.ToString() ?? "", "", ""]);
        }

        string[] headers = showStacks
            ? ["MgdID", "OSID", "State / Frame", "GC Mode", "Exception"]
            : ["Managed ID", "OS Thread ID", "State", "GC Mode", "Exception"];
        sink.Table(headers, rows);
    }

    static bool IsLikelyBlocked(ClrThread t) =>
        t.EnumerateStackTrace().Take(5).Any(f =>
        {
            var name = f.Method?.Name ?? string.Empty;
            return name is "WaitOne" or "Wait" or "Enter" or "TryEnter" or "Join" or "Acquire"
                || name.Contains("Wait", StringComparison.OrdinalIgnoreCase);
        });
}
