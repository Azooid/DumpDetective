using Microsoft.Diagnostics.Runtime;
using System.Text.RegularExpressions;

namespace DumpDetective.Core.Utilities;

public static class DumpHelpers
{
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
        >= 1_048_576     => $"{bytes / 1_048_576.0:F2} MB",
        >= 1_024         => $"{bytes / 1_024.0:F2} KB",
        _                => $"{bytes} B"
    };

    public static bool IsSystemType(string name) =>
        name.StartsWith("System.",                StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Microsoft.",             StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("MS.",                    StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Internal.",              StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Windows.",               StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Interop.",               StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("FxResources.",           StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("System_Private_CoreLib", StringComparison.OrdinalIgnoreCase);

    public static bool IsExceptionType(ClrType type)
    {
        for (var t = type; t != null; t = t.BaseType)
            if (t.Name == "System.Exception")
                return true;
        return false;
    }

    public static (ClrRuntime? Runtime, DataTarget DataTarget) OpenDump(string dumpPath)
    {
        var dataTarget = DataTarget.LoadDump(dumpPath);
        var runtime    = dataTarget.ClrVersions.FirstOrDefault()?.CreateRuntime();
        return (runtime, dataTarget);
    }

    public static string SegmentKindLabel(ClrHeap heap, ulong address)
    {
        var seg = heap.GetSegmentByAddress(address);
        return seg is not null ? SegmentKindLabel(seg) : "Gen";
    }

    public static string SegmentKindLabel(Microsoft.Diagnostics.Runtime.ClrSegment seg) => seg.Kind switch
    {
        GCSegmentKind.Generation0 => "Gen0",
        GCSegmentKind.Generation1 => "Gen1",
        GCSegmentKind.Generation2 => "Gen2",
        GCSegmentKind.Ephemeral   => "Ephemeral",
        GCSegmentKind.Large       => "LOH",
        GCSegmentKind.Pinned      => "POH",
        GCSegmentKind.Frozen      => "Frozen",
        _                         => "Gen"
    };

    /// <summary>
    /// Parses a hex address string (with or without leading <c>0x</c>) into a <see cref="ulong"/>.
    /// </summary>
    public static bool TryParseHex(string s, out ulong value)
    {
        s = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        return ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out value);
    }

    // ── Stack frame name sanitizer ────────────────────────────────────────────

    // Matches compiler-generated suffixes:
    //   <MethodName>b__N_M   → lambda in MethodName
    //   <MethodName>d__N     → async state machine for MethodName
    //   <MethodName>g__LocalN_M → local function LocalN inside MethodName
    private static readonly Regex _asyncStateMachine =
        new(@"<([^>]+)>d__\d+", RegexOptions.Compiled);
    private static readonly Regex _lambda =
        new(@"<([^>]+)>b__\d+(?:_\d+)?", RegexOptions.Compiled);
    private static readonly Regex _localFunc =
        new(@"<([^>]+)>g__(\w+)\|\d+_\d+", RegexOptions.Compiled);
    // Compiler-generated closure class: <>c__DisplayClassN or <>c (with optional `N arity suffix)
    private static readonly Regex _closureClass =
        new(@"<>c(?:__DisplayClass\d+(?:_\d+)?)?", RegexOptions.Compiled);
    // CLR class-level generic args: Foo`2[[A,asm],[B,asm]] or Foo`2+Inner[[A,asm]]
    // Runs after _closureClass so `2+[[...]] orphans are handled by _genericArgsOrphanPlus.
    private static readonly Regex _genericArgs =
        new(@"`(\d+)(\+[^\[<\s()+.]+)?(?:\[\[.*?\]\])+", RegexOptions.Compiled | RegexOptions.Singleline);
    // `N+[[...]] orphan: left after _closureClass strips the inner class name
    private static readonly Regex _genericArgsOrphanPlus =
        new(@"`(\d+)\+(?:\[\[.*?\]\])+", RegexOptions.Compiled | RegexOptions.Singleline);
    // Standalone [[...]] on method names (generic method CLR notation, no backtick)
    private static readonly Regex _orphanGenericArgs =
        new(@"\+?\[\[.*?\]\]", RegexOptions.Compiled | RegexOptions.Singleline);
    // Bare `N arity suffix remaining in parameter type strings after generic arg cleanup
    private static readonly Regex _backtickedArity =
        new(@"`\d+", RegexOptions.Compiled);
    // Orphaned >b__N_M: left when <> from closure class name was consumed by _closureClass
    // e.g. <>c..cctor>b__26_0 → after stripping <>c → ..cctor>b__26_0 (no opening <)
    private static readonly Regex _orphanedLambdaSuffix =
        new(@">b__\d+(?:_\d+)?", RegexOptions.Compiled);

    // Counts [[TypeA,AsmA],[TypeB,AsmB]] arity by counting `],[` separators + 1.
    private static int CountGenericArity(string bracketBlock)
    {
        // bracketBlock is the full match including outer [[ and ]]
        int arity = 1;
        for (int i = 0; i < bracketBlock.Length - 1; i++)
            if (bracketBlock[i] == ']' && bracketBlock[i + 1] == ',')
                arity++;
        return arity;
    }

    private static string GenericPlaceholders(int arity) =>
        "<" + string.Join(", ", Enumerable.Repeat("T", arity)) + ">";

    /// <summary>
    /// Makes a raw ClrMD stack frame name human-readable by decoding compiler-generated
    /// symbols, trimming noise, and appending a short annotation where helpful.
    /// </summary>
    public static string SanitizeFrame(string frame)
    {
        if (string.IsNullOrEmpty(frame)) return frame;

        // Step 1: strip compiler closure class noise first so later passes see clean text.
        // <>c__DisplayClassN_M or <>c — may have a `N suffix (handled in step 3).
        frame = _closureClass.Replace(frame, "");

        // Step 2: `N+InnerClass[[...]] or `N[[...]] → <T...> or <T...>+InnerClass
        frame = _genericArgs.Replace(frame, m =>
        {
            int arity = int.Parse(m.Groups[1].Value);
            string inner = m.Groups[2].Value; // "+InnerClass" or empty
            return GenericPlaceholders(arity) + inner;
        });

        // Step 3: `N+[[...]] orphan (closure class was stripped in step 1, leaving `N+[[)]]
        frame = _genericArgsOrphanPlus.Replace(frame, m =>
        {
            int arity = int.Parse(m.Groups[1].Value);
            return GenericPlaceholders(arity);
        });

        // Step 4: standalone [[...]] on method names (CLR generic method notation, no backtick)
        frame = _orphanGenericArgs.Replace(frame, m => GenericPlaceholders(CountGenericArity(m.Value)));

        // Step 5: decode async state machine  <Foo>d__N  → Foo (async)
        frame = _asyncStateMachine.Replace(frame, m => m.Groups[1].Value + " \u27a4async");

        // Step 6: decode lambda  <Foo>b__N_M  → Foo (lambda)
        frame = _lambda.Replace(frame, m => m.Groups[1].Value + " \u27a4lambda");

        // Step 6b: orphaned >b__N_M — when <> closure class stripping consumed the opening <
        //   e.g. ReceiveTarget<T>+<>c..cctor>b__26_0 → after step 1: ..cctor>b__26_0
        frame = _orphanedLambdaSuffix.Replace(frame, " \u27a4lambda");

        // Step 7: decode local function  <Foo>g__Bar|N_M  → Foo.Bar (local)
        frame = _localFunc.Replace(frame, m => m.Groups[1].Value + "." + m.Groups[2].Value + " \u27a4local");

        // Step 8: replace any remaining System.__Canon with T (appears inside <> param types)
        frame = frame.Replace("System.__Canon", "T");

        // Step 9: strip bare `N arity markers from parameter type strings, e.g. Func`1<T> → Func<T>
        frame = _backtickedArity.Replace(frame, "");

        // Step 10: clean up artefacts from stripping: +. → .  and double dots
        frame = frame.Replace("+.", ".");
        while (frame.Contains("..")) frame = frame.Replace("..", ".");
        frame = frame.TrimStart('.');

        // Step 11: normalize ➤ annotations
        frame = frame
            .Replace(" \u27a4async",   " (async)")
            .Replace(" \u27a4lambda",  " (lambda)")
            .Replace(" \u27a4local",   " (local)");

        return frame;
    }
}
